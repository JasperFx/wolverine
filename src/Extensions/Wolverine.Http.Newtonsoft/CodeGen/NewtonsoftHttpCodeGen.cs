using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Newtonsoft.CodeGen;

/// <summary>
///     <see cref="INewtonsoftHttpCodeGen"/> implementation registered by
///     <see cref="WolverineHttpNewtonsoftExtensions.UseNewtonsoftJsonForSerialization"/>.
///     Produces the same Newtonsoft-flavored request-deserialization and
///     response-serialization codegen frames the Wolverine 5.x core
///     Wolverine.Http package emitted inline.
/// </summary>
internal sealed class NewtonsoftHttpCodeGen : INewtonsoftHttpCodeGen
{
    public Variable CreateReadJsonBodyVariable(ParameterInfo parameter)
    {
        return new ReadJsonBodyWithNewtonsoft(parameter).ReturnVariable!;
    }

    public Variable CreateReadJsonBodyVariable(Type requestType)
    {
        return new ReadJsonBodyWithNewtonsoft(requestType).ReturnVariable!;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MethodCall reflects NewtonsoftHttpSerialization.GetMethod(nameof(WriteJsonAsync)) at codegen time. The target method is statically referenced via nameof; the closed-generic type is rooted at codegen time per the AOT guide.")]
    public Frame CreateWriteJsonFrame(Variable resourceVariable)
    {
        var frame = new MethodCall(typeof(NewtonsoftHttpSerialization),
            nameof(NewtonsoftHttpSerialization.WriteJsonAsync));
        frame.Arguments[1] = resourceVariable;
        return frame;
    }
}
