using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Mcp;

public sealed class BrokerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private long _requestId;

    private BrokerClient(Process process, NamedPipeClientStream pipe)
    {
        _process = process;
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
    }

    public static async Task<BrokerClient> StartAsync(CancellationToken cancellationToken)
    {
        var pipeName = $"windows-computer-use-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var brokerPath = LocateBroker();
        var start = new ProcessStartInfo
        {
            FileName = brokerPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start native Windows broker.");
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                await Console.Error.WriteLineAsync($"[broker] {line}");
        }, cancellationToken);

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000, cancellationToken);
            return new BrokerClient(process, pipe);
        }
        catch
        {
            pipe.Dispose();
            if (!process.HasExited) process.Kill(true);
            process.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId).ToString();
        var request = new BrokerRequest(id, method, arguments.Clone());
        await _writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
        var line = await _reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("Native broker disconnected.");
        var response = JsonSerializer.Deserialize<BrokerResponse>(line, ProtocolJson.Options)
            ?? throw new InvalidDataException("Native broker returned an empty response.");
        if (response.Error is not null)
            throw new InvalidOperationException($"{response.Error.Code}: {response.Error.Message}{(response.Error.Data is null ? "" : Environment.NewLine + response.Error.Data)}");
        return response.Result is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(response.Result, ProtocolJson.Options);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _writer.DisposeAsync(); } catch { }
        _reader.Dispose();
        _pipe.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                if (!await Task.Run(() => _process.WaitForExit(1200))) _process.Kill(true);
            }
        }
        catch { }
        _process.Dispose();
    }

    private static string LocateBroker()
    {
        var configured = Environment.GetEnvironmentVariable("WCU_BROKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var sibling = Path.Combine(AppContext.BaseDirectory, "WindowsComputerUse.Broker.exe");
        if (File.Exists(sibling)) return sibling;
        throw new FileNotFoundException("WindowsComputerUse.Broker.exe was not found. Run scripts/build.ps1 or set WCU_BROKER_PATH.");
    }
}
