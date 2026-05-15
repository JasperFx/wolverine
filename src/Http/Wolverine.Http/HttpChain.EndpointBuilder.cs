using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http.Resources;

namespace Wolverine.Http;

public partial class HttpChain : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> _builderConfigurations = new();
    private readonly List<Action<EndpointBuilder>> _finallyBuilderConfigurations = new();

    /// <summary>
    /// Configure ASP.Net Core endpoint metadata
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public RouteHandlerBuilder Metadata { get; }

    public void Add(Action<EndpointBuilder> convention)
    {
        _builderConfigurations.Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        _finallyBuilderConfigurations.Add(finallyConvention);
    }

    private bool tryApplyAsEndpointMetadataProvider(Type? type, RouteEndpointBuilder builder)
    {
        if (type != null && type.CanBeCastTo(typeof(IEndpointMetadataProvider)))
        {
            var applier = typeof(Applier<>).CloseAndBuildAs<IApplier>(type);
            applier.Apply(builder, Method.Method);

            return true;
        }

        return false;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "QuickBuild closes [FromKeyedServices] parameters via CloseAndBuildAs. _handlerType is populated from the generated handler assembly's ExportedTypes; constructors are emitted by codegen. AOT consumers pre-generate handlers via TypeLoadMode.Static.")]
    [UnconditionalSuppressMessage("Trimming", "IL2077",
        Justification = "_handlerType is populated from the generated handler assembly; constructors are emitted by codegen so they survive trimming in any practical setup.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "QuickBuild closes IFinder<TParameter> via MakeGenericType + Activator.CreateInstance; AOT consumers run pre-generated handlers via TypeLoadMode.Static.")]
    private HttpHandler buildHandler()
    {
        this.InitializeSynchronously(_parent.Rules, _parent, _parent.Container.Services);

        if (_handlerType == null)
        {
            throw new InvalidOperationException(
                $"Failed to resolve the generated handler type for endpoint {_fileName} " +
                $"({string.Join(", ", _httpMethods)} {RoutePattern?.RawText}) " +
                $"on handler type {Method.HandlerType.FullNameInCode()}. " +
                $"The generated source code was:\n{_generatedType?.SourceCode}");
        }

        return (HttpHandler)_parent.Container.QuickBuild(_handlerType);
    }

    public RouteEndpoint BuildEndpoint(RouteWarmup warmup)
    {
        if (Endpoint != null) return Endpoint;

        RequestDelegate? requestDelegate = null;
        if (_parent.Rules.TypeLoadMode == TypeLoadMode.Static && !DynamicCodeBuilder.WithinCodegenCommand)
        {
            var handler = buildHandler();
            requestDelegate = handler.Handle;
        }
        else
        {
            if (warmup == RouteWarmup.Eager && !DynamicCodeBuilder.WithinCodegenCommand)
            {
                var handler = buildHandler();
                requestDelegate = c => handler.Handle(c);
            }
            else
            {
                var handler = new Lazy<HttpHandler>(buildHandler);
                requestDelegate = c => handler.Value.Handle(c);
            }
        }

        var builder = new RouteEndpointBuilder(requestDelegate, RoutePattern!, Order)
        {
            DisplayName = DisplayName
        };

        establishResourceTypeMetadata(builder);
        foreach (var configuration in _builderConfigurations) configuration(builder);
        foreach (var finallyConfiguration in _finallyBuilderConfigurations) finallyConfiguration(builder);

        foreach (var parameter in Method.Method.GetParameters())
        {
            tryApplyAsEndpointMetadataProvider(parameter.ParameterType, builder);
        }

        foreach (var created in Middleware.SelectMany(x => x.Creates))
        {
            tryApplyAsEndpointMetadataProvider(created.VariableType, builder);
        }

        // Set up OpenAPI data for ProblemDetails with status code 400 if not already exists
        if (Middleware.SelectMany(x => x.Creates).Any(x => x.VariableType == typeof(ProblemDetails)))
        {
            if (!builder.Metadata.OfType<WolverineProducesResponseTypeMetadata>()
                    .Any(x => x.Type != null && x.Type.CanBeCastTo<ProblemDetails>()))
            {
                builder.Metadata.Add(new ProducesProblemDetailsResponseTypeMetadata());
            }
        }

        if (RouteName.IsNotEmpty())
        {
            builder.Metadata.Add(new RouteNameMetadata(RouteName));
        }

        if (HasExplicitOperationId)
        {
            builder.Metadata.Add(new EndpointNameMetadata(OperationId));
        }

        if (EndpointSummary.IsNotEmpty())
        {
            builder.Metadata.Add(new EndpointSummaryAttribute(EndpointSummary));
        }

        if (EndpointDescription.IsNotEmpty())
        {
            builder.Metadata.Add(new EndpointDescriptionAttribute(EndpointDescription));
        }

        Endpoint = (RouteEndpoint?)builder.Build();
        return Endpoint!;
    }

    private void establishResourceTypeMetadata(RouteEndpointBuilder builder)
    {
        if (tryApplyAsEndpointMetadataProvider(ResourceType, builder)) return;

        if (ResourceType == null || ResourceType == typeof(void) || ResourceType.FullName == "Microsoft.FSharp.Core.Unit")
        {
            Metadata.Produces(204);
            return;
        }

        if (ResourceType.CanBeCastTo<ISideEffect>())
        {
            Metadata.Produces(204);
            return;
        }

        if (ResourceType == typeof(string))
        {
            Metadata.Produces(200, typeof(string), "text/plain");
            return;
        }

        Metadata.Produces(200, ResourceType, "application/json");
        Metadata.Produces(404);
    }

    internal interface IApplier
    {
        void Apply(EndpointBuilder builder, MethodInfo method);
    }

    internal class Applier<T> : IApplier where T : IEndpointMetadataProvider
    {
        public void Apply(EndpointBuilder builder, MethodInfo method)
        {
            T.PopulateMetadata(method, builder);
        }
    }
}

internal class ProducesProblemDetailsResponseTypeMetadata : IProducesResponseTypeMetadata
{
    public Type? Type => typeof(ProblemDetails);
    public int StatusCode => 400;
    public IEnumerable<string> ContentTypes => new string[] {"application/problem+json" };
}