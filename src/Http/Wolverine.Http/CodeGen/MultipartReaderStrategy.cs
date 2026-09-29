using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

/// <summary>
///     Binds a <see cref="MultipartReader" /> parameter over the raw request body, so the endpoint streams
///     the multipart sections instead of having the whole form buffered by <c>Request.Form</c>.
/// </summary>
internal class MultipartReaderStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.ParameterType != typeof(MultipartReader))
        {
            variable = null;
            return false;
        }

        // The body can be read once, so middleware and the handler share one reader
        var existing = chain.ChainVariables.FirstOrDefault(x => x.VariableType == typeof(MultipartReader));
        if (existing != null)
        {
            variable = existing;
            return true;
        }

        chain.StreamsMultipartBody = true;

        var frame = new ReadMultipartBody(parameter);
        chain.Middleware.Add(frame);
        variable = frame.Variable;
        chain.ChainVariables.Add(variable);

        return true;
    }
}

internal class ReadMultipartBody : AsyncFrame
{
    private Variable? _httpContext;

    public ReadMultipartBody(ParameterInfo parameter)
    {
        Variable = new Variable(typeof(MultipartReader), parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Stream the multipart request body without buffering the form");
        writer.Write(
            $"var ({Variable.Usage}, multipartContinue) = await {nameof(HttpHandler.ReadMultipartAsync)}({_httpContext!.Usage}).ConfigureAwait(false);");
        writer.Write(
            $"if (multipartContinue == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)}) return;");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Stream the multipart request body without buffering the form");

        // Unlike ReadJsonAsync, which is an inherited *instance* method and so is qualified with the
        // member's `this` self identifier (jasperfx#393), ReadMultipartAsync is static -- so it is
        // qualified by TYPE, the way WriteStringFrame already does it. The C# emit above can leave it
        // bare only because the generated handler derives from HttpHandler.
        //
        // ReadMultipartAsync returns ValueTask<(MultipartReader?, HandlerContinuation)> -- a struct
        // (value) tuple, which F# destructures as `let! struct (a, b) =`; the reference-tuple form is
        // an FS0001 at compile time.
        writer.Write(
            $"let! struct ({Variable.Usage}, multipartContinue) = {typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.ReadMultipartAsync)}({_httpContext!.Usage})");

        // F# has no early `return`, so the abort guard renders the rest of the chain in its `else`.
        var condition =
            $"multipartContinue = {typeof(HandlerContinuation).FSharpName()}.{nameof(HandlerContinuation.Stop)}";
        FSharpEmitHelpers.WriteAbortGuard(writer, method, condition, Next);
    }
}
