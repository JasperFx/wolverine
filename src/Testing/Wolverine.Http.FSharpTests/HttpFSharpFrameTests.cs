using Shouldly;
using Xunit;

namespace Wolverine.Http.FSharpTests;

/// <summary>
///     Unit-level checks on the F# code emitted by specific Wolverine.Http frames,
///     complementing the compile gate that proves the full output builds. Each test calls
///     <see cref="HttpFSharpCodegenSample.GenerateCode" /> (cached via a static lazy) and
///     asserts on the presence or absence of specific F# patterns.
/// </summary>
public class HttpFSharpFrameTests
{
    private static readonly Lazy<string> _code =
        new(HttpFSharpCodegenSample.GenerateCode, LazyThreadSafetyMode.ExecutionAndPublication);

    private string Code => _code.Value;

    [Fact]
    public void read_json_body_emits_struct_tuple_destructuring()
    {
        // ReadJsonAsync<T> returns ValueTask<(T?, HandlerContinuation)> — a VALUE (struct) tuple.
        // F# requires `let! struct (a, b) = expr` for struct tuple destructuring; the reference
        // tuple form `let! (a, b) = expr` causes FS0001 at compile time.
        Code.ShouldContain("let! struct (");
        Code.ShouldNotContain("let! (command, jsonContinue)");
    }

    [Fact]
    public void write_endpoint_types_uses_runtime_type_resolution()
    {
        // typeof<> in F# cannot resolve F# module types (only class/record/DU types).
        // WriteEndpointTypesFrame.GenerateFSharpCode() uses Type.GetType(assemblyQualifiedName)
        // so F# module types defined in application assemblies resolve correctly.
        Code.ShouldContain("Array.choose (fun n -> System.Type.GetType(n)");
    }

    [Fact]
    public void query_string_binding_emits_explicit_type_annotation()
    {
        // Without a type annotation, F# type inference can pick an unintended record type when
        // field names are ambiguous across opened namespaces (e.g. Platform.Core.SqlBroadcast vs
        // an endpoint-local InboxQueryRequest both having Skip/Take fields). QueryStringBindingFrame
        // now emits `let var : FullType = { ... }` to pin the inferred type unambiguously.
        Code.ShouldContain("let thingFilter : Wolverine.Http.FSharpContracts.ThingFilter =");
    }
}
