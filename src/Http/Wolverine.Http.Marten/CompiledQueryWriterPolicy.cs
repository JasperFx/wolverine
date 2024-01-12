using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Marten.Linq;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.Resources;

namespace Wolverine.Http.Marten;

#nullable enable

public class CompiledQueryWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        bool ImplementsGenericInterface(Type type, Type assigned)
        {
            return type.GetInterfaces().Any(x =>
                x.IsGenericType && x.GetGenericTypeDefinition() == assigned);
        }

        var result = chain.Method.Creates.FirstOrDefault();
        if (result is null) return false;
        if (ImplementsGenericInterface(result.VariableType, typeof(ICompiledListQuery<,>)))
        {
            chain.Postprocessors.Add(new MartenWriteArrayCodeFrame(result));
            return true;
        }

        if (ImplementsGenericInterface(result.VariableType, typeof(ICompiledQuery<,>)))
        {
            chain.Postprocessors.Add(new MartenWriteJsonCodeFrame(result));
            return true;
        }

        return false;
    }
}

public class MartenWriteJsonCodeFrame : AsyncFrame
{
    private Variable? _documentSession;
    private Variable? _httpContext;
    private readonly Variable _compiledQuery;

    public MartenWriteJsonCodeFrame(Variable variable)
    {
        _compiledQuery = variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Run the compiled query and stream the response");
        writer.Write($"await Marten.AspNetCore.QueryableExtensions.WriteOne({_documentSession?.Usage}, {_compiledQuery.Usage}, {_httpContext?.Usage});");
        Next?.GenerateCode(method, writer);
    }
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _documentSession = chain.FindVariable(typeof(IDocumentSession));
        yield return _documentSession;
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }
}

public class MartenWriteArrayCodeFrame : AsyncFrame
{
    private Variable? _documentSession;
    private Variable? _httpContext;
    private readonly Variable _compiledQuery;

    public MartenWriteArrayCodeFrame(Variable variable)
    {
        _compiledQuery = variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Run the compiled query and stream the response");
        writer.Write($"await Marten.AspNetCore.QueryableExtensions.WriteArray({_documentSession?.Usage}, {_compiledQuery.Usage}, {_httpContext?.Usage});");
        Next?.GenerateCode(method, writer);
    }
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _documentSession = chain.FindVariable(typeof(IDocumentSession));
        yield return _documentSession;
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }
}

#nullable disable