using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Resources;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Http;

public partial class HttpChain
{
    /// <summary>
    /// Used to cache variables like for IFormFile or IFormFileCollection
    /// that might be reused between middleware and handler methods, but should
    /// not be created more than once
    /// </summary>
    public List<Variable> ChainVariables { get; } = new();
    
    internal string? SourceCode => _generatedType?.SourceCode;

    private readonly object _locker = new();

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        if (_generatedType != null)
        {
            return;
        }
        
        lock (_locker)
        {
            if (_generatedType != null) return;

            assembly.UsingNamespaces!.Fill(typeof(RoutingHttpContextExtensions).Namespace);
            assembly.UsingNamespaces.Fill("System.Linq");
            assembly.UsingNamespaces.Fill("System");

            _generatedType = assembly.AddType(_fileName!, typeof(HttpHandler));

            assembly.ReferenceAssembly(Method.HandlerType.Assembly);
            assembly.ReferenceAssembly(typeof(HttpContext).Assembly);
            assembly.ReferenceAssembly(typeof(HttpChain).Assembly);

            var handleMethod = _generatedType.MethodFor(nameof(HttpHandler.Handle));

            handleMethod.DerivedVariables.AddRange(HttpContextVariables);

            var loggedType = determineLogMarkerType();

            handleMethod.Sources.Add(new LoggerVariableSource(loggedType));
            handleMethod.Sources.Add(new MessageBusSource());

            handleMethod.Frames.AddRange(DetermineFrames(assembly.Rules));
        }
    }

    private Type determineLogMarkerType()
    {
        if (HasRequestType) return RequestType;

        if (Method.HandlerType.IsStatic()) return typeof(HttpGraph);

        return Method.HandlerType;
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
        Debug.WriteLine(_generatedType?.SourceCode);

        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == _fileName)
            ?? assembly.GetTypes().FirstOrDefault(x => x.Name == _fileName);

        return _handlerType != null;
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

        if (TryInferMessageIdentity(out var identity))
        {
            if (AuditedMembers.All(x => x.Member != identity))
            {
                Audit(identity);
            }    
        }
        
        if (AuditedMembers.Count != 0)
        {
            Middleware.Insert(0, new AuditToActivityFrame(this));
        }

        var index = 0;
        foreach (var frame in Middleware)
        {
            // Try to add route value parameters
            if (frame is MethodCall call)
            {
                RouteParameterStrategy.TryApplyRouteVariables(this, call);
            }

            foreach (var result in frame.Creates.Where(x => x.VariableType.CanBeCastTo<IResult>()))
            {
                result.OverrideName("result" + ++index);
            }

            foreach (var details in frame.Creates.Where(x => x.VariableType.CanBeCastTo<ProblemDetails>()))
            {
                details.OverrideName(details.Usage + ++index);
            }

            yield return frame;
        }

        foreach (var f in _parent.Container.TryCreateConstructorFrames(new MethodCall[]{this.Method}))
        {
            yield return f;
        }

        yield return Method;

        var actionsOnOtherReturnValues = (NoContent ? Method.Creates : Method.Creates.Skip(1))
            .Select(x => x.ReturnAction(this)).SelectMany(x => x.Frames()).ToArray();
        foreach (var frame in actionsOnOtherReturnValues) yield return frame;

        if (requiresFlush(actionsOnOtherReturnValues))
        {
            var flush = Postprocessors.OfType<FlushOutgoingMessages>().FirstOrDefault();
            if (flush != null)
            {
                Postprocessors.Remove(flush);
            }

            flush ??= new FlushOutgoingMessages();
            Postprocessors.Add(flush);
        }
        
        foreach (var frame in Postprocessors) yield return frame;
    }

    private bool requiresFlush(Frame[] actionsOnOtherReturnValues)
    {
        if (Postprocessors.Any(x => x.MaySendMessages())) return true;
        if (actionsOnOtherReturnValues.Any(x => x.MaySendMessages())) return true;

        var dependencies = ServiceDependencies(_parent.Container, []).ToArray();
        if (dependencies.Contains(typeof(IMessageBus))) return true;
        if (dependencies.Contains(typeof(IMessageContext))) return true;

        return false;
    }

    private string determineFileName()
    {
        var parts = RoutePattern.RawText.Replace("{", "").Replace("*", "").Replace(".", "_").Replace("?", "").Replace("}", "").Split('/').Select(x => x.Split(':').First());

        char[] invalidPathChars = Path.GetInvalidPathChars();
        var fileName = _httpMethods.Select(x => x.ToUpper()).Concat(parts).Join("_").Replace('-', '_').Replace("__", "_");

        var characters = fileName.ToCharArray();
        for (int i = 0; i < characters.Length; i++)
        {
            if (invalidPathChars.Contains(characters[i]))
            {
                characters[i] = '_';
            }
        }
        
        return new string(characters);
    }
}