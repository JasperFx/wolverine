using System.ServiceModel;
using ProtoBuf.Grpc;
using Wolverine.Grpc;
using Wolverine.Grpc.Tests.ExplicitRegistration;

// GH-4396, option 2. This is the only [assembly: WolverineGrpcCodeFirstContract] in the test assembly, and
// it registers the contract for EVERY fixture that scans this assembly. The contract is therefore backed by
// a real handler and has no implementing class, so it behaves like any attributed test contract elsewhere.
[assembly: WolverineGrpcCodeFirstContract<IAssemblyRegisteredService>]

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

/// <summary>
///     <c>[ServiceContract]</c> only; registered by the assembly attribute above rather than by
///     <c>WolverineGrpcOptions.IncludeCodeFirstContract</c> or <c>[WolverineGrpcService]</c>.
/// </summary>
[ServiceContract]
public interface IAssemblyRegisteredService
{
    Task<ExplicitReply> Echo(AssemblyRegisteredRequest request, CallContext context = default);
}

[ProtoBuf.ProtoContract]
public class AssemblyRegisteredRequest
{
    [ProtoBuf.ProtoMember(1)] public string Text { get; set; } = string.Empty;
}

public static class AssemblyRegisteredEchoHandler
{
    public static ExplicitReply Handle(AssemblyRegisteredRequest request)
        => new() { Echo = $"assembly:{request.Text}" };
}
