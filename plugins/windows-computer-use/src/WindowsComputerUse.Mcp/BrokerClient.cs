using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Mcp;

public sealed class BrokerClient : IAsyncDisposable
{
    private const int DefaultCallTimeoutMs = 180_000;
    private const int RecoveryTimeoutMs = 15_000;
    private readonly string _brokerPath;
    private readonly int? _callTimeoutMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _heldKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _heldButtons = new(StringComparer.OrdinalIgnoreCase);
    private BrokerConnection? _connection;
    private bool _inputRecoveryPending;
    private long _requestId;
    private bool _disposed;

    private BrokerClient(string brokerPath, int? callTimeoutMs)
    {
        _brokerPath = brokerPath;
        _callTimeoutMs = callTimeoutMs;
    }

    public static async Task<BrokerClient> StartAsync(CancellationToken cancellationToken)
    {
        var client = new BrokerClient(LocateBroker(), ParseCallTimeout(Environment.GetEnvironmentVariable("WCU_BROKER_CALL_TIMEOUT_MS")));
        try
        {
            client._connection = await BrokerConnection.StartAsync(client._brokerPath, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connection ??= await BrokerConnection.StartAsync(_brokerPath, cancellationToken);
            if (_inputRecoveryPending) await RecoverPendingInputAsync(cancellationToken);
            var id = Interlocked.Increment(ref _requestId).ToString();
            BrokerResponse response;
            using var deadline = CreateCallDeadline(cancellationToken);
            try
            {
                response = await SendAsync(_connection, new BrokerRequest(id, method, arguments.Clone()), deadline?.Token ?? cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline?.IsCancellationRequested == true)
            {
                var recovery = await RestartAndRecoverAsync(method, arguments);
                var timeout = _callTimeoutMs is int value ? $"{value} ms" : "the configured deadline";
                throw new TimeoutException(
                    $"Native broker call '{method}' exceeded {timeout}. Its final UI state is unknown; do not replay it blindly. " +
                    $"The broker was {(recovery.Restarted ? "restarted" : "not restarted")} and tracked input was {(recovery.InputRecovered ? "released" : "not fully released")}. " +
                    $"Re-observe the target before continuing; if this was a clipboard action, verify clipboard state too.{recovery.ErrorSuffix}");
            }
            catch (Exception error) when (IsTransportFailure(error))
            {
                var recovery = await RestartAndRecoverAsync(method, arguments);
                throw new IOException(
                    $"Native broker transport failed during '{method}'. Its final UI state is unknown; do not replay it blindly. " +
                    $"The broker was {(recovery.Restarted ? "restarted" : "not restarted")} and tracked input was {(recovery.InputRecovered ? "released" : "not fully released")}. " +
                    $"Re-observe the target before continuing; if this was a clipboard action, verify clipboard state too.{recovery.ErrorSuffix}", error);
            }

            if (!string.Equals(response.Id, id, StringComparison.Ordinal))
            {
                var recovery = await RestartAndRecoverAsync(method, arguments);
                throw new InvalidDataException(
                    $"Native broker returned response id '{response.Id}' for request '{id}'. The protocol session was reset; " +
                    $"broker restarted={recovery.Restarted}, tracked input released={recovery.InputRecovered}. Re-observe before continuing.{recovery.ErrorSuffix}");
            }
            if (response.Error is not null)
                throw new InvalidOperationException($"{response.Error.Code}: {response.Error.Message}{(response.Error.Data is null ? "" : Environment.NewLine + response.Error.Data)}");

            UpdateTrackedInput(method, arguments);
            return response.Result is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(response.Result, ProtocolJson.Options);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_connection is not null)
            {
                await _connection.StopAsync(forceKill: false);
                _connection = null;
            }
            _heldKeys.Clear();
            _heldButtons.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static int? ParseCallTimeout(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultCallTimeoutMs;
        if (!int.TryParse(configured, out var milliseconds) || milliseconds < 0)
            throw new InvalidOperationException("WCU_BROKER_CALL_TIMEOUT_MS must be a non-negative integer. Use 0 to disable broker call deadlines.");
        return milliseconds == 0 ? null : milliseconds;
    }

    private CancellationTokenSource? CreateCallDeadline(CancellationToken cancellationToken)
    {
        if (_callTimeoutMs is not int timeoutMs) return null;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeoutMs);
        return source;
    }

    private async Task<RecoveryResult> RestartAndRecoverAsync(string uncertainMethod, JsonElement uncertainArguments)
    {
        var keys = new HashSet<string>(_heldKeys, StringComparer.OrdinalIgnoreCase);
        var buttons = new HashSet<string>(_heldButtons, StringComparer.OrdinalIgnoreCase);
        AddUncertainInput(uncertainMethod, uncertainArguments, keys, buttons);
        _heldKeys.UnionWith(keys);
        _heldButtons.UnionWith(buttons);
        _inputRecoveryPending = keys.Count > 0 || buttons.Count > 0;

        if (_connection is not null)
        {
            await _connection.StopAsync(forceKill: true);
            _connection = null;
        }

        try
        {
            using var recoveryDeadline = new CancellationTokenSource(RecoveryTimeoutMs);
            _connection = await BrokerConnection.StartAsync(_brokerPath, recoveryDeadline.Token);
            await RecoverPendingInputAsync(recoveryDeadline.Token);
            return new RecoveryResult(true, true, null);
        }
        catch (Exception error)
        {
            if (_connection is not null)
            {
                await _connection.StopAsync(forceKill: true);
                _connection = null;
            }
            return new RecoveryResult(false, false, error.Message);
        }
    }

    private async Task RecoverPendingInputAsync(CancellationToken cancellationToken)
    {
        if (!_inputRecoveryPending) return;
        if (_connection is null) throw new InvalidOperationException("Cannot recover tracked input without a broker connection.");
        var recoveryRequest = new BrokerRequest(
            Interlocked.Increment(ref _requestId).ToString(),
            "recover_input_state",
            JsonSerializer.SerializeToElement(new { keys = _heldKeys.ToArray(), buttons = _heldButtons.ToArray() }, ProtocolJson.Options));
        var response = await SendAsync(_connection, recoveryRequest, cancellationToken);
        if (!string.Equals(response.Id, recoveryRequest.Id, StringComparison.Ordinal))
            throw new InvalidDataException($"Native broker returned response id '{response.Id}' for recovery request '{recoveryRequest.Id}'.");
        if (response.Error is not null)
            throw new InvalidOperationException($"{response.Error.Code}: {response.Error.Message}");
        _heldKeys.Clear();
        _heldButtons.Clear();
        _inputRecoveryPending = false;
    }

    private static async Task<BrokerResponse> SendAsync(BrokerConnection connection, BrokerRequest request, CancellationToken cancellationToken)
    {
        await connection.Writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options).AsMemory(), cancellationToken);
        var line = await connection.Reader.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException("Native broker disconnected.");
        return JsonSerializer.Deserialize<BrokerResponse>(line, ProtocolJson.Options)
            ?? throw new InvalidDataException("Native broker returned an empty response.");
    }

    private static bool IsTransportFailure(Exception error) => error is IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException;

    private void UpdateTrackedInput(string method, JsonElement arguments)
    {
        switch (method)
        {
            case "key_down":
                if (ArgumentString(arguments, "key") is { } downKey) _heldKeys.Add(downKey);
                break;
            case "key_up":
                if (ArgumentString(arguments, "key") is { } upKey) _heldKeys.Remove(upKey);
                break;
            case "mouse_down":
                _heldButtons.Add(NormalizeButton(ArgumentString(arguments, "button")));
                break;
            case "mouse_up":
                _heldButtons.Remove(NormalizeButton(ArgumentString(arguments, "button")));
                break;
            case "end_session":
                _heldKeys.Clear();
                _heldButtons.Clear();
                break;
        }
    }

    private static void AddUncertainInput(string method, JsonElement arguments, ISet<string> keys, ISet<string> buttons)
    {
        if (method == "key_down" && ArgumentString(arguments, "key") is { } key) keys.Add(key);
        if (method is "mouse_down" or "drag") buttons.Add(NormalizeButton(ArgumentString(arguments, "button")));
    }

    private static string? ArgumentString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeButton(string? button) => button?.ToLowerInvariant() switch
    {
        null or "left" or "l" or "primary" => "left",
        "right" or "r" or "secondary" => "right",
        "middle" or "m" or "auxiliary" => "middle",
        "x1" or "back" => "x1",
        "x2" or "forward" => "x2",
        _ => button.ToLowerInvariant()
    };

    private static string LocateBroker()
    {
        var configured = Environment.GetEnvironmentVariable("WCU_BROKER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var sibling = Path.Combine(AppContext.BaseDirectory, "WindowsComputerUse.Broker.exe");
        if (File.Exists(sibling)) return sibling;
        throw new FileNotFoundException("WindowsComputerUse.Broker.exe was not found. Run scripts/build.ps1 or set WCU_BROKER_PATH.");
    }

    private sealed class BrokerConnection
    {
        private readonly Process _process;
        private readonly NamedPipeClientStream _pipe;
        private readonly Task _stderrDrain;
        private bool _stopped;

        private BrokerConnection(Process process, NamedPipeClientStream pipe, Task stderrDrain)
        {
            _process = process;
            _pipe = pipe;
            _stderrDrain = stderrDrain;
            Reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
            Writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
        }

        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }

        public static async Task<BrokerConnection> StartAsync(string brokerPath, CancellationToken cancellationToken)
        {
            var pipeName = $"windows-computer-use-{Environment.ProcessId}-{Guid.NewGuid():N}";
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
            var stderrDrain = DrainStderrAsync(process);
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(10_000, cancellationToken);
                return new BrokerConnection(process, pipe, stderrDrain);
            }
            catch
            {
                pipe.Dispose();
                try { if (!process.HasExited) process.Kill(true); } catch { }
                try { await stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                process.Dispose();
                throw;
            }
        }

        public async Task StopAsync(bool forceKill)
        {
            if (_stopped) return;
            _stopped = true;
            if (forceKill)
            {
                try { if (!_process.HasExited) _process.Kill(true); } catch { }
            }
            try { _pipe.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
            try { Writer.Dispose(); } catch { }
            try
            {
                if (!_process.HasExited && !await Task.Run(() => _process.WaitForExit(forceKill ? 3_000 : 1_200)))
                    _process.Kill(true);
            }
            catch { }
            try { await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _process.Dispose();
        }

        private static async Task DrainStderrAsync(Process process)
        {
            var forward = Environment.GetEnvironmentVariable("WCU_DEBUG") == "1";
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (forward) await Console.Error.WriteLineAsync($"[broker] {line}");
                }
            }
            catch (Exception) when (process.HasExited)
            {
                // Process shutdown closes the redirected stream.
            }
        }
    }

    private sealed record RecoveryResult(bool Restarted, bool InputRecovered, string? Error)
    {
        public string ErrorSuffix => string.IsNullOrWhiteSpace(Error) ? string.Empty : $" Recovery error: {Error}";
    }
}
