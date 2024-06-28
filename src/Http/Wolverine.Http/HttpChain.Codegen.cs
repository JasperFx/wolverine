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
        lock (_locker)
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
        
        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == _fileName);

        if (_handlerType == null)
        {
            return false;
        }

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

        foreach (var frame in Postprocessors) yield return frame;

        if (!Postprocessors.OfType<MethodCall>().Any(x =>
                x.HandlerType == typeof(MessageContext) &&
                x.Method.Name == nameof(MessageContext.EnqueueCascadingAsync)))
        {
            if (actionsOnOtherReturnValues.OfType<CaptureCascadingMessages>().Any() && !Postprocessors.OfType<MethodCall>().Any(x => x.Method.Name == nameof(MessageContext.FlushOutgoingMessagesAsync)))
            {
                var flush = MethodCall.For<MessageContext>(x => x.FlushOutgoingMessagesAsync());
                flush.CommentText = "Making sure there is at least one call to flush outgoing, cascading messages";
                yield return flush;
            }
        }
    }

    private string determineFileName()
    {
        var parts = RoutePattern.RawText.Replace("{", "").Replace("*", "").Replace("}", "").Split('/').Select(x => x.Split(':').First());

        return _httpMethods.Select(x => x.ToUpper()).Concat(parts).Join("_").Replace('-', '_').Replace("__", "_");
    }
}