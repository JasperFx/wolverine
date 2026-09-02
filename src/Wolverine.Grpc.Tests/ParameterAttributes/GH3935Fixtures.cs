using System.Reflection;
using System.ServiceModel;
using Grpc.Core;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using ProtoBuf;
using ProtoBuf.Grpc;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Grpc.Tests.ParameterAttributes.Generated;
using IServiceContainer = JasperFx.IServiceContainer;

namespace Wolverine.Grpc.Tests.ParameterAttributes;

/// <summary>
/// GH-3935: a test-only <see cref="WolverineParameterAttribute"/>. The real family
/// (<c>[Entity]</c>, <c>[All]</c>, <c>[Queryable]</c>, the aggregate attributes) all resolve through
/// a persistence provider, and none is registered in this project — but every one of them reaches
/// the chain through the same <see cref="WolverineParameterAttribute.TryApply"/> call, so what is
/// actually under test is whether the gRPC chains make that call at all. This attribute answers with
/// a literal expression, which needs no declaration frame and is therefore visible in the generated
/// source and observable at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class GH3935ValueAttribute : WolverineParameterAttribute
{
    public const string Marker = "GH3935_APPLIED";

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        return new Variable(parameter.ParameterType, $"\"{Marker}\"");
    }
}

// ---------------------------------------------------------------------------
// Proto-first
// ---------------------------------------------------------------------------

[WolverineGrpcService]
public abstract partial class ParameterAttributeStub : ParameterAttributeTest.ParameterAttributeTestBase
{
    [WolverineBefore]
    public static void BeforeWithParameterAttribute([GH3935Value] string marker)
    {
        GH3935Sink.Record("proto-first:before", marker);
    }

    [WolverineAfter]
    public static void AfterWithParameterAttribute([GH3935Value] string marker)
    {
        GH3935Sink.Record("proto-first:after", marker);
    }
}

// ---------------------------------------------------------------------------
// Code-first. Hooks live on the HANDLER class for this flavour — see
// CodeFirstGrpcServiceChain's middleware discovery.
// ---------------------------------------------------------------------------

[ServiceContract]
[WolverineGrpcService]
public interface IGH3935CodeFirstService
{
    Task<GH3935Reply> Echo(GH3935Request request, CallContext context = default);
}

[ProtoContract]
public class GH3935Request
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
}

[ProtoContract]
public class GH3935Reply
{
    [ProtoMember(1)] public string Message { get; set; } = string.Empty;
}

public class GH3935CodeFirstHandler
{
    [WolverineBefore]
    public static void Before([GH3935Value] string marker)
    {
        GH3935Sink.Record("code-first:before", marker);
    }

    [WolverineAfter]
    public static void After([GH3935Value] string marker)
    {
        GH3935Sink.Record("code-first:after", marker);
    }

    public static GH3935Reply Handle(GH3935Request request) => new() { Message = request.Name };
}

// ---------------------------------------------------------------------------
// Hand-written
// ---------------------------------------------------------------------------

[ServiceContract]
public interface IGH3935HandWrittenContract
{
    Task<GH3935HandWrittenReply> Echo(GH3935HandWrittenRequest request, CallContext context = default);
}

[ProtoContract]
public class GH3935HandWrittenRequest
{
    [ProtoMember(1)] public string Name { get; set; } = string.Empty;
}

[ProtoContract]
public class GH3935HandWrittenReply
{
    [ProtoMember(1)] public string Message { get; set; } = string.Empty;
}

// Named with the GrpcService suffix on purpose -- that is the hand-written discovery predicate
// (GrpcGraph.IsHandWrittenServiceClass), and the contract interface must NOT carry
// [WolverineGrpcService] or the generated-implementation path claims it instead.
public class GH3935HandWrittenGrpcService : IGH3935HandWrittenContract
{
    [WolverineBefore]
    public static void Before([GH3935Value] string marker)
    {
        GH3935Sink.Record("hand-written:before", marker);
    }

    [WolverineAfter]
    public static void After([GH3935Value] string marker)
    {
        GH3935Sink.Record("hand-written:after", marker);
    }

    public Task<GH3935HandWrittenReply> Echo(GH3935HandWrittenRequest request, CallContext context = default)
        => Task.FromResult(new GH3935HandWrittenReply { Message = request.Name });
}

/// <summary>Collects what the hooks were actually handed, for the runtime half of the tests.</summary>
public static class GH3935Sink
{
    private static readonly List<(string Hook, string Value)> _entries = new();

    public static void Record(string hook, string value)
    {
        lock (_entries) _entries.Add((hook, value));
    }

    public static (string Hook, string Value)[] Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public static void Clear()
    {
        lock (_entries) _entries.Clear();
    }
}

/// <summary>Handler for the proto-first stub's RPC, which forwards to the message bus.</summary>
public class GH3935ProtoFirstHandler
{
    public static Generated.ParamReply Handle(Generated.ParamRequest request)
        => new() { Message = request.Name };
}
