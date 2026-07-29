using WindowsComputerUse.Mcp;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("windows-computer-use MCP requires Windows.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var broker = await BrokerClient.StartAsync(cancellation.Token);
await using var server = new McpServer(broker);
await server.RunAsync(cancellation.Token);
return 0;
