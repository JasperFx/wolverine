using System.Runtime.CompilerServices;
using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

/// <summary>
///     GH-4396. A code-first contract that carries <c>[ServiceContract]</c> only. There is deliberately
///     no <c>[WolverineGrpcService]</c> here: the attribute scan must NOT find this interface, and the
///     only way it reaches the generated-implementation path is
///     <c>WolverineGrpcOptions.IncludeCodeFirstContract</c>. Unary and server-streaming shapes only:
///     the implementing class below must also be wrappable as a hand-written service in the fixtures
///     that do not register this contract, and that wrapper delegates neither client-streaming nor
///     (on the generated path) bidirectional methods.
/// </summary>
[ServiceContract]
public interface IExplicitlyRegisteredService
{
    Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default);
    IAsyncEnumerable<ExplicitReply> EchoStream(ExplicitStreamRequest request, CallContext context = default);
}

[ProtoContract]
public class ExplicitRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
}

[ProtoContract]
public class ExplicitStreamRequest
{
    [ProtoMember(1)] public string Text { get; set; } = string.Empty;
    [ProtoMember(2)] public int Count { get; set; }
}

[ProtoContract]
public class ExplicitReply
{
    [ProtoMember(1)] public string Echo { get; set; } = string.Empty;
}

public static class ExplicitEchoHandler
{
    public static ExplicitReply Handle(ExplicitRequest request)
        => new() { Echo = $"registered:{request.Text}" };
}

public static class ExplicitEchoStreamHandler
{
    public static async IAsyncEnumerable<ExplicitReply> Handle(
        ExplicitStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ExplicitReply { Echo = $"{request.Text}:{i}" };
            await Task.Yield();
        }
    }
}

/// <summary>
///     The double-mapping guard subject. Named with the <c>GrpcService</c> suffix so the hand-written
///     discovery predicate matches it. In a host that does NOT register
///     <see cref="IExplicitlyRegisteredService"/> this is an ordinary hand-written service and gets its
///     own delegation wrapper. In a host that DOES register the contract, the generated implementation
///     owns the contract and this class must be left alone: not wrapped, not direct-mapped. Its
///     replies are prefixed differently from the handler's so a test can tell which one answered.
/// </summary>
public class ExplicitlyRegisteredEchoGrpcService : IExplicitlyRegisteredService
{
    public Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default)
        => Task.FromResult(new ExplicitReply { Echo = $"hand-written:{request.Text}" });

    public async IAsyncEnumerable<ExplicitReply> EchoStream(ExplicitStreamRequest request,
        CallContext context = default)
    {
        for (var i = 0; i < request.Count; i++)
        {
            yield return new ExplicitReply { Echo = $"hand-written:{request.Text}:{i}" };
            await Task.Yield();
        }
    }
}

/// <summary>
///     Conflict-guard subject: a concrete class marked <c>[WolverineGrpcService]</c> that implements a
///     <c>[ServiceContract]</c> interface with no attribute of its own. Left unregistered it is a valid
///     hand-written service (which is why it has a working implementation, so the fixtures that scan
///     this assembly stay healthy). Once the contract is registered through
///     <c>IncludeCodeFirstContract</c>, discovery must refuse the combination.
/// </summary>
[ServiceContract]
public interface IConflictRegisteredContract
{
    Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default);
}

[WolverineGrpcService]
public class ConflictRegisteredImpl : IConflictRegisteredContract
{
    public Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default)
        => Task.FromResult(new ExplicitReply { Echo = request.Text });
}

/// <summary>Registration-validation subjects. None of these are gRPC services.</summary>
public interface INotAServiceContract
{
    Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default);
}

[ServiceContract]
public interface IOpenGenericContract<T>
{
    Task<T> Echo(ExplicitRequest request, CallContext context = default);
}

[ServiceContract]
public class NotAnInterfaceContract;
