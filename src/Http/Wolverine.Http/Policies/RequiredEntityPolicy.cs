using System.ComponentModel.DataAnnotations;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Policies;

internal class RequiredEntityPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains)
        {
            var requiredParameters = chain.Method.Method.GetParameters()
                .Where(x => x.HasAttribute<RequiredAttribute>() && x.ParameterType.IsClass).ToArray();

            if (requiredParameters.Any())
            {
                chain.Metadata.Produces(404);

                foreach (var parameter in requiredParameters)
                {
                    var frame = new SetStatusCodeAndReturnFrame(parameter.ParameterType);
                    chain.Middleware.Add(frame);
                }
            }
        }
    }
}

internal class SetStatusCodeAndReturnFrame : SyncFrame
{
    private readonly Type _entityType;
    private Variable _httpResponse;
    private Variable _entity;

    public SetStatusCodeAndReturnFrame(Type entityType)
    {
        _entityType = entityType;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("404 if this required object is null");
        writer.Write($"BLOCK:if ({_entity.Usage} == null)");
        writer.Write($"{_httpResponse.Usage}.{nameof(HttpResponse.StatusCode)} = 404;");
        if (method.AsyncMode == AsyncMode.ReturnCompletedTask)
        {
            writer.Write($"return {typeof(Task).FullNameInCode()}.{nameof(Task.CompletedTask)};");
        }
        else
        {
            writer.Write("return;");
        }
        
        writer.FinishBlock();
        
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _entity = chain.FindVariable(_entityType);
        yield return _entity;
        
        _httpResponse = chain.FindVariable(typeof(HttpResponse));
        yield return _httpResponse;
    }
}