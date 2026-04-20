using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using GreeterWithGrpcErrors.Messages;
using Grpc.Core;
using Grpc.Net.Client;
using ProtoBuf.Grpc.Client;

// Allow HTTP/2 over plain HTTP for the local sample (no trusted dev cert required).
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5005");
var greeter = channel.CreateGrpcService<IGreeterService>();

Console.WriteLine("=== Valid call ===");
var ok = await greeter.Greet(new GreetRequest { Name = "Erik", Age = 30 });
Console.WriteLine($"  -> {ok.Message}");

Console.WriteLine();
Console.WriteLine("=== Validation failure (BadRequest) ===");
try
{
    await greeter.Greet(new GreetRequest { Name = "", Age = -1 });
}
catch (RpcException ex)
{
    PrintRichError(ex);
}

Console.WriteLine();
Console.WriteLine("=== Domain exception (PreconditionFailure) ===");
try
{
    await greeter.Farewell(new FarewellRequest { Name = "voldemort" });
}
catch (RpcException ex)
{
    PrintRichError(ex);
}

static void PrintRichError(RpcException ex)
{
    Console.WriteLine($"  gRPC status : {ex.StatusCode}");

    // GetRpcStatus() pulls google.rpc.Status out of the grpc-status-details-bin trailer.
    // Returns null when the server didn't attach rich details.
    var richStatus = ex.GetRpcStatus();
    if (richStatus is null)
    {
        Console.WriteLine("  (no rich details attached)");
        return;
    }

    Console.WriteLine($"  rich code   : {(Code)richStatus.Code}");

    // The AIP-193 detail payloads live as packed Any messages. Unpack each one by
    // matching its descriptor — this is the gRPC analogue of reading
    // ValidationProblemDetails / ProblemDetails fields on the HTTP side.
    foreach (var detail in richStatus.Details)
    {
        if (detail.Is(BadRequest.Descriptor))
        {
            var badRequest = detail.Unpack<BadRequest>();
            Console.WriteLine("  BadRequest:");
            foreach (var violation in badRequest.FieldViolations)
            {
                Console.WriteLine($"    - {violation.Field}: {violation.Description}");
            }
        }
        else if (detail.Is(PreconditionFailure.Descriptor))
        {
            var precondition = detail.Unpack<PreconditionFailure>();
            Console.WriteLine("  PreconditionFailure:");
            foreach (var violation in precondition.Violations)
            {
                Console.WriteLine($"    - [{violation.Type}] {violation.Subject}: {violation.Description}");
            }
        }
        else
        {
            Console.WriteLine($"  (unhandled detail type: {detail.TypeUrl})");
        }
    }
}
