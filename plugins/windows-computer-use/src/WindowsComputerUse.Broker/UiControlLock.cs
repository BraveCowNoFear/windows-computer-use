using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsComputerUse.Broker;

internal sealed class UiControlLock : IDisposable
{
    private static readonly string LockPath = Environment.GetEnvironmentVariable("CODEX_UI_CONTROL_LOCK_FILE")
        ?? Path.Combine(Path.GetTempPath(), "codex-ui-control.lock.json");
    private static readonly string GuardPath = LockPath + ".guard";
    private readonly string _token;
    private bool _disposed;

    private UiControlLock(string token) => _token = token;

    public static UiControlLock Acquire(string owner, TimeSpan? timeout = null, TimeSpan? ttl = null)
    {
        var timeoutValue = timeout ?? TimeSpan.FromSeconds(120);
        var ttlValue = ttl ?? TimeSpan.FromSeconds(180);
        var deadline = DateTimeOffset.UtcNow + timeoutValue;
        var token = Guid.NewGuid().ToString("N");
        while (true)
        {
            using (AcquireGuard(TimeSpan.FromSeconds(5)))
            {
                var existing = ReadRecord(LockPath);
                if (existing is null || existing.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    TryDelete(LockPath);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    var record = new Dictionary<string, object?>
                    {
                        ["token"] = token,
                        ["owner"] = owner,
                        ["pid"] = Environment.ProcessId,
                        ["threadId"] = Environment.GetEnvironmentVariable("CODEX_THREAD_ID") ?? string.Empty,
                        ["createdAt"] = now,
                        ["updatedAt"] = now,
                        ["expiresAt"] = now + ttlValue.TotalSeconds
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
                    try
                    {
                        using var stream = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        JsonSerializer.Serialize(stream, record, WindowsComputerUse.Contracts.ProtocolJson.Options);
                        return new UiControlLock(token);
                    }
                    catch (IOException)
                    {
                        // Another controller won the atomic create; retry.
                    }
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"UI control lock is busy: {LockPath}");
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var guard = AcquireGuard(TimeSpan.FromSeconds(5));
            var record = ReadRecord(LockPath);
            if (record?.Token == _token) TryDelete(LockPath);
        }
        catch
        {
            // Lock expiry remains a bounded cleanup fallback.
        }
    }

    private static IDisposable AcquireGuard(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GuardPath)!);
                using (var stream = new FileStream(GuardPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        pid = Environment.ProcessId,
                        createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                        expiresAt = DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds() / 1000.0
                    }, WindowsComputerUse.Contracts.ProtocolJson.Options));
                    stream.Write(payload);
                }
                return new GuardLease();
            }
            catch (IOException)
            {
                var guard = ReadRecord(GuardPath);
                if (guard is null || guard.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) TryDelete(GuardPath);
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException($"UI control metadata lock is busy: {GuardPath}");
                Thread.Sleep(20);
            }
        }
    }

    private static LockRecord? ReadRecord(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            return new LockRecord(
                root.TryGetProperty("token", out var token) ? token.GetString() : null,
                root.TryGetProperty("expiresAt", out var expires) && expires.TryGetDouble(out var value) ? value : 0);
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }

    private sealed record LockRecord(string? Token, double ExpiresAt);

    private sealed class GuardLease : IDisposable
    {
        public void Dispose() => TryDelete(GuardPath);
    }
}
