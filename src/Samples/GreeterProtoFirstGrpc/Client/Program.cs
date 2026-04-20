using GreeterProtoFirstGrpc.Messages;
using Grpc.Core;
using Grpc.Net.Client;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5003");

// The generated client class (Greeter.GreeterClient) comes from the shared Messages
// project — proto-first contracts give you a strongly-typed stub on the client with no
// hand-written code.
var client = new Greeter.GreeterClient(channel);

// Unary
var hello = await client.SayHelloAsync(new HelloRequest { Name = "Erik" });
Console.WriteLine($"SayHello -> {hello.Message}");

var bye = await client.SayGoodbyeAsync(new GoodbyeRequest { Name = "Erik" });
Console.WriteLine($"SayGoodbye -> {bye.Message}");

// Server streaming
Console.WriteLine("StreamGreetings ->");
using var call = client.StreamGreetings(new StreamGreetingsRequest { Name = "Erik", Count = 5 });
await foreach (var reply in call.ResponseStream.ReadAllAsync())
{
    Console.WriteLine($"  {reply.Message}");
}

// Exception mapping (AIP-193): handler throws KeyNotFoundException → client sees NotFound.
try
{
    await client.FaultAsync(new FaultRequest { Kind = "key" });
}
catch (RpcException ex)
{
    Console.WriteLine($"Fault('key') -> RpcException: {ex.StatusCode} ({ex.Status.Detail})");
}
