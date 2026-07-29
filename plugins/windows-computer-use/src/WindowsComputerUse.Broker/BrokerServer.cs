using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class BrokerServer
{
    private readonly string _pipeName;

    public BrokerServer(string pipeName) => _pipeName = pipeName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(cancellationToken);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };
        using var dispatcher = new BrokerDispatcher();
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                BrokerResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<BrokerRequest>(line, ProtocolJson.Options)
                        ?? throw new InvalidDataException("Broker request was empty.");
                    var result = await dispatcher.DispatchAsync(request.Method, request.Params, cancellationToken);
                    response = BrokerResponse.Ok(request.Id, result);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    string id;
                    try { id = JsonDocument.Parse(line).RootElement.GetProperty("id").GetString() ?? "unknown"; }
                    catch { id = "unknown"; }
                    var debug = Environment.GetEnvironmentVariable("WCU_DEBUG") == "1" ? error.ToString() : null;
                    response = BrokerResponse.Fail(id, error.GetType().Name, error.Message, debug);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, ProtocolJson.Options));
            }
        }
        catch (IOException) when (!pipe.IsConnected)
        {
            // A client closing its stdio session is a normal broker shutdown.
        }
        finally
        {
            try
            {
                await writer.DisposeAsync();
            }
            catch (IOException) when (!pipe.IsConnected)
            {
                // StreamWriter flush after peer disconnect is also normal.
            }
        }
    }
}
