using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http.Resources;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Http;

public partial class HttpChain
{
    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        assembly.UsingNamespaces!.Fill(typeof(RoutingHttpContextExtensions).Namespace);
        assembly.UsingNamespaces.Fill("System.Linq");
        assembly.UsingNamespaces.Fill("System");

        _generatedType = assembly.AddType(_fileName!, typeof(HttpHandler));

        assembly.ReferenceAssembly(Method.HandlerType.Assembly);
        assembly.ReferenceAssembly(typeof(HttpContext).Assembly);
        assembly.ReferenceAssembly(typeof(HttpChain).Assembly);

        var handleMethod = _generatedType.MethodFor(nameof(HttpHandler.Handle));

        handleMethod.DerivedVariables.AddRange(HttpContextVariables);

        var loggedType = InputType() ?? (Method.HandlerType.IsStatic() ? typeof(HttpGraph) : Method.HandlerType);

        handleMethod.Sources.Add(new LoggerVariableSource(loggedType));

        handleMethod.Frames.AddRange(DetermineFrames(assembly.Rules));
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        var found = this.As<ICodeFile>().AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == _fileName);

        if (_handlerType == null)
        {
            return false;
        }
        
        Debug.WriteLine(_generatedType?.SourceCode);

        return true;
    }

    string ICodeFile.FileName => _fileName!;

    internal IEnumerable<Frame> DetermineFrames(GenerationRules rules)
    {
        // Add frames for any writers
        if (ResourceType != typeof(void))
        {
            foreach (var writerPolicy in _parent.WriterPolicies)
            {
                if (writerPolicy.TryApply(this))
                {
                    break;
                }
            }
        }
        else
        {
            Postprocessors.Add(new WriteEmptyBodyStatusCode());
        }

        foreach (var frame in ContextModifiers)
        {
            yield return frame;
        }

        var index = 0;
        foreach (var frame in Middleware)
        {
            foreach (var result in frame.Creates.Where(x => x.VariableType.CanBeCastTo<IResult>()))
            {
                result.OverrideName("result" + ++index);
            }
            
            yield return frame;
        }

        yield return Method;

        var actionsOnOtherReturnValues = (NoContent ? Method.Creates : Method.Creates.Skip(1))
            .Select(x => x.ReturnAction(this)).SelectMany(x => x.Frames());
        foreach (var frame in actionsOnOtherReturnValues) yield return frame;

        foreach (var frame in Postprocessors) yield return frame;
    }

    private string determineFileName()
    {
        var parts = RoutePattern.RawText.Replace("{", "").Replace("}", "").Split('/').Select(x => x.Split(':').First());
        
        return _httpMethods.Select(x => x.ToUpper()).Concat(parts).Join("_").Replace("-", "_").Replace("__", "_");
    }
}