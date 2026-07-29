using WindowsComputerUse.Broker;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("windows-computer-use broker requires Windows.");
    return 2;
}

try { NativeMethods.SetProcessDpiAwarenessContext(new nint(-4)); } catch { }
var pipeIndex = Array.FindIndex(args, value => value.Equals("--pipe", StringComparison.OrdinalIgnoreCase));
if (pipeIndex < 0 || pipeIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[pipeIndex + 1]))
{
    Console.Error.WriteLine("Usage: WindowsComputerUse.Broker --pipe <name>");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
await new BrokerServer(args[pipeIndex + 1]).RunAsync(cancellation.Token);
return 0;
