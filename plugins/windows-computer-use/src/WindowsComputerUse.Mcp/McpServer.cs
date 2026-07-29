using System.Text;
using System.Text.Json;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Mcp;

public sealed class McpServer : IAsyncDisposable
{
    private readonly BrokerClient _broker;
    private readonly StreamReader _input = new(Console.OpenStandardInput(), new UTF8Encoding(false));
    private readonly StreamWriter _output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    public McpServer(BrokerClient broker) => _broker = broker;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            await HandleAsync(line, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _input.Dispose();
        await _output.DisposeAsync();
        await _broker.DisposeAsync();
    }

    private async Task HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonElement id = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var hasId = root.TryGetProperty("id", out id);
            var method = root.GetProperty("method").GetString() ?? throw new InvalidDataException("JSON-RPC method is missing.");
            if (!hasId) return;
            object result = method switch
            {
                "initialize" => Initialize(root),
                "ping" => new { },
                "tools/list" => new { tools = ToolCatalog.All },
                "tools/call" => await CallToolAsync(root, cancellationToken),
                "shutdown" => new { },
                _ => throw new NotSupportedException($"Unsupported MCP method: {method}")
            };
            await WriteAsync(new { jsonrpc = "2.0", id = id.Clone(), result });
        }
        catch (Exception error)
        {
            object? responseId = id.ValueKind == JsonValueKind.Undefined ? null : id.Clone();
            await WriteAsync(new
            {
                jsonrpc = "2.0",
                id = responseId,
                error = new { code = -32000, message = error.Message }
            });
        }
    }

    private static object Initialize(JsonElement root)
    {
        var requested = root.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("protocolVersion", out var protocol)
            ? protocol.GetString()
            : null;
        return new
        {
            protocolVersion = requested ?? "2025-06-18",
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "windows-computer-use", version = "0.19.0" },
            instructions = "Full-control Windows MCP. Prefer UIA; use desktop=true keyboard input only for the unchanged current foreground focus; use atomic paste_text/copy_text for reversible clipboard transfer; use wait_for_window for dialogs; bind visual pixels to their screenshot id; restore minimized windows before vision."
        };
    }

    private async Task<object> CallToolAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var parameters = root.GetProperty("params");
        var name = parameters.GetProperty("name").GetString() ?? throw new InvalidDataException("Tool name is missing.");
        if (!ToolCatalog.All.Any(tool => tool.Name == name)) throw new NotSupportedException($"Unknown tool: {name}");
        var arguments = parameters.TryGetProperty("arguments", out var supplied) ? supplied.Clone() : ProtocolJson.EmptyObject;
        try
        {
            var result = await _broker.CallAsync(name, arguments, cancellationToken);
            if (name is "capture" or "snapshot")
            {
                var snapshot = name == "snapshot"
                    ? result.Deserialize<WindowStateSnapshot>(ProtocolJson.Options)
                    : null;
                var capture = snapshot?.Capture ?? result.Deserialize<CaptureResult>(ProtocolJson.Options)
                    ?? throw new InvalidDataException("Capture result was empty.");
                var metadata = name == "snapshot"
                    ? JsonSerializer.Serialize(new
                    {
                        snapshot!.Inspection,
                        Capture = new
                        {
                            capture.Id,
                            capture.Width,
                            capture.Height,
                            capture.Bounds,
                            capture.Backend,
                            capture.CapturedAt,
                            capture.Sha256,
                            capture.Path
                        }
                    }, ProtocolJson.Options)
                    : JsonSerializer.Serialize(new
                    {
                        capture.Id,
                        capture.Width,
                        capture.Height,
                        capture.Bounds,
                        capture.Backend,
                        capture.CapturedAt,
                        capture.Sha256,
                        capture.Path
                    }, ProtocolJson.Options);
                return new
                {
                    content = new object[]
                    {
                        new { type = "text", text = metadata },
                        new { type = "image", data = capture.Data, mimeType = capture.MimeType }
                    }
                };
            }
            return new { content = new[] { new { type = "text", text = result.GetRawText() } } };
        }
        catch (Exception error)
        {
            return new { content = new[] { new { type = "text", text = error.Message } }, isError = true };
        }
    }

    private Task WriteAsync(object response) => _output.WriteLineAsync(JsonSerializer.Serialize(response, ProtocolJson.Options));
}
