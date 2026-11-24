using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Ino.Cli;

public sealed record Credentials(string ApiKey, string Secret);

/// <summary>Key + secret per profile in the OS keychain. A 0600 file store exists only on request (`profile add --store file`); CI uses environment variables.</summary>
public interface ISecretStore
{
    void Write(string profile, Credentials credentials);
    Credentials? Read(string profile);
    void Delete(string profile);
}

public static class SecretStores
{
    public const string Service = "ino-cli";

    public static ISecretStore ForPlatform() =>
        OperatingSystem.IsMacOS() ? new MacKeychain() :
        OperatingSystem.IsWindows() ? new WindowsCredentialManager() :
        OperatingSystem.IsLinux() ? new LinuxSecretTool() : new NoSecretStore();

    internal static byte[] Encode(Credentials c) => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["apiKey"] = c.ApiKey, ["secret"] = c.Secret });

    internal static Credentials? Decode(byte[] bytes)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
            return d is not null && d.TryGetValue("apiKey", out var k) && d.TryGetValue("secret", out var s) ? new Credentials(k, s) : null;
        }
        catch (JsonException) { return null; }
    }

    internal static CliException Unavailable(string detail) => new(ExitCodes.Usage, "NO_KEYCHAIN",
        $"The system keychain is not available ({detail}). Add the profile with --store file (a 0600 file, weaker than a keychain), or use environment variables: INO_BASE_URL, INO_CHANNEL, INO_API_KEY, INO_HMAC_SECRET, INO_ENVIRONMENT.");
}

sealed class NoSecretStore : ISecretStore
{
    public void Write(string profile, Credentials credentials) => throw SecretStores.Unavailable("unsupported platform");
    public Credentials? Read(string profile) => null;
    public void Delete(string profile) { }
}

/// <summary>macOS: a generic password item (service ino-cli, account = profile) through SecItem*; the secret never appears on a command line.</summary>
[SupportedOSPlatform("macos")]
sealed class MacKeychain : ISecretStore
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const int ErrSecSuccess = 0, ErrSecItemNotFound = -25300, ErrSecDuplicateItem = -25299;
    const uint Utf8 = 0x08000100;

    [DllImport(Security)] static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation)] static extern IntPtr CFStringCreateWithBytes(IntPtr alloc, byte[] bytes, nint length, uint encoding, bool external);
    [DllImport(CoreFoundation)] static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);

    static readonly Lazy<(IntPtr sec, IntPtr cf)> Libs = new(() => (NativeLibrary.Load(Security), NativeLibrary.Load(CoreFoundation)));
    static IntPtr Const(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));

    static IntPtr Str(string s) { var b = Encoding.UTF8.GetBytes(s); return CFStringCreateWithBytes(IntPtr.Zero, b, b.Length, Utf8, false); }

    static IntPtr Dict(List<(IntPtr key, IntPtr value)> pairs)
    {
        var (_, cf) = Libs.Value;
        return CFDictionaryCreate(IntPtr.Zero, pairs.Select(p => p.key).ToArray(), pairs.Select(p => p.value).ToArray(), pairs.Count,
            NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
    }

    static T WithQuery<T>(string profile, Func<IntPtr, IntPtr, IntPtr, T> body)
    {
        var (sec, _) = Libs.Value;
        var service = Str(SecretStores.Service); var account = Str(profile);
        try
        {
            var q = Dict([(Const(sec, "kSecClass"), Const(sec, "kSecClassGenericPassword")), (Const(sec, "kSecAttrService"), service), (Const(sec, "kSecAttrAccount"), account)]);
            try { return body(q, service, account); } finally { CFRelease(q); }
        }
        finally { CFRelease(service); CFRelease(account); }
    }

    public void Write(string profile, Credentials credentials)
    {
        var (sec, _) = Libs.Value;
        var bytes = SecretStores.Encode(credentials);
        var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        Array.Clear(bytes);
        try
        {
            var status = WithQuery(profile, (q, service, account) =>
            {
                var attrs = Dict([(Const(sec, "kSecValueData"), data)]);
                try
                {
                    var s = SecItemUpdate(q, attrs);
                    if (s != ErrSecItemNotFound) return s;
                    var add = Dict([(Const(sec, "kSecClass"), Const(sec, "kSecClassGenericPassword")), (Const(sec, "kSecAttrService"), service),
                                    (Const(sec, "kSecAttrAccount"), account), (Const(sec, "kSecValueData"), data)]);
                    try { return SecItemAdd(add, IntPtr.Zero); } finally { CFRelease(add); }
                }
                finally { CFRelease(attrs); }
            });
            if (status is not (ErrSecSuccess or ErrSecDuplicateItem)) throw SecretStores.Unavailable($"keychain error {status}");
        }
        finally { CFRelease(data); }
    }

    public Credentials? Read(string profile)
    {
        var (sec, cf) = Libs.Value;
        return WithQuery(profile, (_, service, account) =>
        {
            var q = Dict([(Const(sec, "kSecClass"), Const(sec, "kSecClassGenericPassword")), (Const(sec, "kSecAttrService"), service), (Const(sec, "kSecAttrAccount"), account),
                          (Const(sec, "kSecReturnData"), Const(cf, "kCFBooleanTrue")), (Const(sec, "kSecMatchLimit"), Const(sec, "kSecMatchLimitOne"))]);
            try
            {
                var s = SecItemCopyMatching(q, out var result);
                if (s == ErrSecItemNotFound) return null;
                if (s != ErrSecSuccess) throw SecretStores.Unavailable($"keychain error {s}");
                try
                {
                    var bytes = new byte[CFDataGetLength(result)];
                    Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, bytes.Length);
                    var c = SecretStores.Decode(bytes);
                    Array.Clear(bytes);
                    return c;
                }
                finally { CFRelease(result); }
            }
            finally { CFRelease(q); }
        });
    }

    public void Delete(string profile) => WithQuery(profile, (q, _, _) => SecItemDelete(q));
}

/// <summary>Windows: a generic credential "ino-cli:&lt;profile&gt;" in Credential Manager (per user).</summary>
[SupportedOSPlatform("windows")]
sealed class WindowsCredentialManager : ISecretStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type; public string TargetName; public string? Comment; public long LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount; public IntPtr Attributes; public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL cred, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);
    const uint Generic = 1, PersistLocalMachine = 2;

    static string Target(string profile) => $"{SecretStores.Service}:{profile}";

    public void Write(string profile, Credentials credentials)
    {
        var bytes = SecretStores.Encode(credentials);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL { Type = Generic, TargetName = Target(profile), CredentialBlob = blob, CredentialBlobSize = (uint)bytes.Length, Persist = PersistLocalMachine, UserName = profile };
            if (!CredWriteW(ref c, 0)) throw SecretStores.Unavailable($"Credential Manager error {Marshal.GetLastWin32Error()}");
        }
        finally { Array.Clear(bytes); Marshal.FreeHGlobal(blob); }
    }

    public Credentials? Read(string profile)
    {
        if (!CredReadW(Target(profile), Generic, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            var result = SecretStores.Decode(bytes);
            Array.Clear(bytes);
            return result;
        }
        finally { CredFree(p); }
    }

    public void Delete(string profile) => CredDeleteW(Target(profile), Generic, 0);
}

/// <summary>Linux: libsecret through `secret-tool`; the secret goes over stdin, never on the command line.</summary>
sealed class LinuxSecretTool : ISecretStore
{
    static (int code, string stdout) Run(string[] args, string? stdin = null)
    {
        var psi = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { throw SecretStores.Unavailable("secret-tool is not installed; install libsecret-tools"); }
        using (p)
        {
            if (stdin is not null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
    }

    public void Write(string profile, Credentials credentials)
    {
        var (code, _) = Run(["store", "--label", $"Ino CLI ({profile})", "service", SecretStores.Service, "account", profile], Encoding.UTF8.GetString(SecretStores.Encode(credentials)));
        if (code != 0) throw SecretStores.Unavailable("secret-tool store failed; is a keyring (GNOME Keyring, KWallet) running?");
    }

    public Credentials? Read(string profile)
    {
        var (code, output) = Run(["lookup", "service", SecretStores.Service, "account", profile]);
        return code == 0 && output.Length > 0 ? SecretStores.Decode(Encoding.UTF8.GetBytes(output.TrimEnd('\n'))) : null;
    }

    public void Delete(string profile) => Run(["clear", "service", SecretStores.Service, "account", profile]);
}
