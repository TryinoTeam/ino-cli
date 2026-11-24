using System.Text.Json.Nodes;

namespace Connector;

/// <summary>
/// Offline queue: when Ino cannot be reached, a sale is written here with its original time and sent later in order.
/// The engine accepts back-dated sales within the channel's window (default 7 days); transactionDate keeps the real sale time.
/// </summary>
public sealed class OfflineQueue
{
    readonly string _file;
    readonly SemaphoreSlim _lock = new(1, 1);

    public OfflineQueue(string file) { _file = file; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!); }

    public void Add(string operation, JsonObject body)
    {
        _lock.Wait();
        try { File.AppendAllText(_file, new JsonObject { ["operation"] = operation, ["body"] = body.DeepClone() }.ToJsonString() + "\n"); }
        finally { _lock.Release(); }
    }

    public List<JsonObject> Items() => File.Exists(_file)
        ? File.ReadAllLines(_file).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList() : [];

    /// <summary>Sends queued items in order; stops at the first one Ino does not accept so order is kept. Returns how many were sent.</summary>
    public async Task<int> FlushAsync(Func<string, JsonObject, Task<InoClient.Result>> call)
    {
        await _lock.WaitAsync();
        try
        {
            var items = Items();
            var sent = 0;
            foreach (var it in items)
            {
                var r = await call(it["operation"]!.GetValue<string>(), it["body"]!.AsObject());
                var code = (r.Body as JsonObject)?["ResponseCode"]?.GetValue<string>();
                var done = (r.Status is >= 200 and < 300 && code is null or "SUCCESS") || code == "CONFLICT";   // CONFLICT: already recorded
                if (!done) break;
                sent++;
            }
            var tmp = _file + ".tmp";
            await File.WriteAllLinesAsync(tmp, items.Skip(sent).Select(i => i.ToJsonString()));
            File.Move(tmp, _file, overwrite: true);
            return sent;
        }
        finally { _lock.Release(); }
    }
}
