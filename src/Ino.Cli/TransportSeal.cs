using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>
/// IL3b-3 (docs/INTEGRATION_LAYER.md §8): a secret sealed for the engine exactly as Power Apps seals it: a fresh AES-256-GCM key encrypts the secret
/// (the tag after the ciphertext), the engine's RSA transport key (SPKI, from GET /manage/v1/transport-key) encrypts that key with OAEP-SHA256.
/// Only the engine that issued the key can open it; the secret never travels or is logged in plain text.
/// </summary>
public static class TransportSeal
{
    public static JsonObject Seal(string kid, string publicKeySpki, string secret)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(secret);
        var (cipher, tag) = (new byte[plain.Length], new byte[16]);
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, plain, cipher, tag);
            return new JsonObject { ["kid"] = kid, ["ek"] = B64(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256)), ["iv"] = B64(iv), ["ct"] = B64([.. cipher, .. tag]) };
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The tool's key from an environment variable or a file (never from the command line, T18). A file others can read gets a warning.
    /// </summary>
    public static string ReadKey(string? envName, string? file, TextWriter err, Func<string, string?>? env = null)
    {
        if ((envName is null) == (file is null))
            throw new CliException(ExitCodes.Usage, "NO_KEY_SOURCE", "Give the tool's API key with --key-env <VARIABLE> or --key-file <path> (never on the command line).");
        string? value;
        if (envName is not null)
        {
            value = (env ?? Environment.GetEnvironmentVariable)(envName);
            if (string.IsNullOrWhiteSpace(value)) throw new CliException(ExitCodes.Usage, "NO_KEY", $"The variable {envName} is empty or not set.");
        }
        else
        {
            var info = new FileInfo(file!);
            if (!info.Exists) throw new CliException(ExitCodes.Usage, "NO_KEY", $"{file} does not exist.");
            if (info.LinkTarget is not null) throw new CliException(ExitCodes.Usage, "BAD_KEY_FILE", $"{file} must be a regular file (not a link).");
            // Review (IL3b-3 K/S 3): opened once; the handle is checked (a regular file, not a device or FIFO) and at most 16 KB is read from it.
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            try { handle = File.OpenHandle(file!, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliException(ExitCodes.Usage, "NO_KEY", $"{file} cannot be read ({e.Message})."); }
            using (handle)
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(handle) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
                    err.WriteLine($"warning: {file} can be read by other users; keep it with mode 0600 (chmod 600 {file}).");
                var length = RandomAccess.GetLength(handle);
                if (length <= 0 || length > 16_384) throw new CliException(ExitCodes.Usage, "BAD_KEY_FILE", $"{file} must be a regular file of at most 16 KB.");
                var buffer = new byte[length];
                var read = RandomAccess.Read(handle, buffer, 0);
                value = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            }
            if (value.Length == 0) throw new CliException(ExitCodes.Usage, "NO_KEY", $"{file} is empty.");
        }
        value = value.Trim();
        if (value.Length > 4000) throw new CliException(ExitCodes.Usage, "KEY_TOO_LONG", "The key is longer than 4000 characters.");
        return value;
    }
}
