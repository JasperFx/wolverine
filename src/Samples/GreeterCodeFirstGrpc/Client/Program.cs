using GreeterCodeFirstGrpc.Messages;
using Grpc.Net.Client;
using ProtoBuf.Grpc.Client;

// Allow plain HTTP/2 for the local sample (no TLS cert required).
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5008");

// protobuf-net.Grpc synthesises a strongly-typed client directly from the
// [ServiceContract] interface — the same interface the server uses.
// No generated stub classes, no .proto file, no protoc invocation.
var greeter = channel.CreateGrpcService<IGreeterCodeFirstService>();

// Unary
var reply = await greeter.Greet(new GreetRequest { Name = "Erik" });
Console.WriteLine($"Greet       -> {reply.Message}");

// Server streaming
Console.WriteLine("StreamGreetings ->");
await foreach (var item in greeter.StreamGreetings(new StreamGreetingsRequest { Name = "Erik", Count = 5 }))
{
    Console.WriteLine($"  {item.Message}");
}

// Client streaming: stream several GreetRequests, get back one folded GreetingSummary.
// The client side is just an IAsyncEnumerable — no stream-writer plumbing needed.
static async IAsyncEnumerable<GreetRequest> Names()
{
    foreach (var name in new[] { "Erik", "Ripley", "Newt" })
    {
        yield return new GreetRequest { Name = name };
        await Task.Yield();
    }
}

var summary = await greeter.CollectGreetings(Names());
Console.WriteLine($"CollectGreetings -> {summary.Message} ({summary.Count} greetings)");
