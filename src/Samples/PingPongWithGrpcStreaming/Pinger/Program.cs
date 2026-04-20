using Grpc.Net.Client;
using PingPongWithGrpcStreaming.Messages;
using ProtoBuf.Grpc.Client;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5002");
var pingStreamService = channel.CreateGrpcService<IPingStreamService>();

Console.WriteLine("Requesting a stream of 5 pongs every 3 seconds. Ctrl+C to exit.");

for (var round = 0; ; round++)
{
    Console.WriteLine($"-- round {round} --");

    try
    {
        var stream = pingStreamService.PingStream(new PingStreamRequest
        {
            Message = $"round-{round}",
            Count = 5
        });

        await foreach (var reply in stream)
        {
            Console.WriteLine($"  <- {reply.Echo} (handled count: {reply.HandledCount})");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(3));
}
