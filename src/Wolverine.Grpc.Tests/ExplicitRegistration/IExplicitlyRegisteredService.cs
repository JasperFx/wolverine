using System.Runtime.CompilerServices;
using System.ServiceModel;
using ProtoBuf;
using ProtoBuf.Grpc;

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

// [ServiceContract] only, so the attribute scan does not find it.
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

// Matches hand-written discovery by its suffix. Must be neither wrapped nor direct-mapped once
// IExplicitlyRegisteredService is registered. The "hand-written:" prefix shows if it answered.
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

[ServiceContract]
public interface IConflictRegisteredContract
{
    Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default);
}

// A valid hand-written service until IConflictRegisteredContract is registered.
[WolverineGrpcService]
public class ConflictRegisteredImpl : IConflictRegisteredContract
{
    public Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default)
        => Task.FromResult(new ExplicitReply { Echo = request.Text });
}

// Types IncludeCodeFirstContract must reject.
public interface INotAServiceContract
{
    Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default);
}

[ServiceContract]
public interface IGenericContract<T>
{
    Task<T> Echo(ExplicitRequest request, CallContext context = default);
}

[ServiceContract]
internal interface IInternalContract
{
    Task<ExplicitReply> Echo(ExplicitRequest request, CallContext context = default);
}

[ServiceContract]
public class NotAnInterfaceContract;
