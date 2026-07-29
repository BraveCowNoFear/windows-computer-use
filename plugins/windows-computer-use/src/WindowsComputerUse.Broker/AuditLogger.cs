using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsComputerUse.Broker;

internal sealed class AuditLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    public AuditLogger()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsComputerUse", "audit");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
    }

    public void Write(string sessionId, string method, JsonElement args, bool ok, long elapsedMs, string? error = null)
    {
        var raw = args.GetRawText();
        var entry = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            session_id = sessionId,
            method,
            ok,
            elapsed_ms = elapsedMs,
            argument_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(),
            error
        }, WindowsComputerUse.Contracts.ProtocolJson.Options);
        lock (_gate) File.AppendAllText(_path, entry + Environment.NewLine, new UTF8Encoding(false));
    }
}
