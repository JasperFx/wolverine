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

    /// <summary>
    /// Indicates whether the endpoint builder for this chain requires access to the application's
    /// service provider. Default value is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// If <see langword="true"/>, the <c>RouteEndpointBuilder</c> used to build this chain's endpoint
    /// will be instantiated with the service provider exposed by the parent <see cref="HttpGraph"/>.
    /// </remarks>
    internal bool RequiresApplicationServices { get; set; }

    public void Add(Action<EndpointBuilder> convention)
    {
        _builderConfigurations.Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        _finallyBuilderConfigurations.Add(finallyConvention);
    }

    private readonly List<Type> _endpointMetadataProviderTypes = [];

    /// <summary>
    ///     GH-4825. The types this chain actually closed <see cref="Applier{T}" /> over while its endpoint
    ///     was built, for the Native AOT rooting block.
    /// </summary>
    /// <remarks>
    ///     Recorded as the closes happen rather than recomputed later, so the rooting block cannot drift
    ///     from the three places <see cref="BuildEndpoint" /> hands a type to
    ///     <c>tryApplyAsEndpointMetadataProvider</c>. <see cref="HttpGraph.BuildFiles" /> reads this after
    ///     <see cref="HttpGraph.DiscoverEndpoints" /> has built every endpoint, which is the only order
    ///     <c>codegen write</c> runs them in.
    ///     <para>
    ///     GH-4841. That ordering is the one assumption this shape makes, and the alternative -- recomputing
    ///     the candidate set from the resource type, the parameters and the middleware's created variables
    ///     -- makes none, but drifts silently the day a fourth call site is added. The recorded form was kept
    ///     because its failure mode is the one that can be checked: <see cref="HttpGraph.BuildFiles" />
    ///     refuses to generate while any chain's <see cref="Endpoint" /> is still null, so "nothing was
    ///     recorded" can never read as "nothing needed rooting".
    ///     </para>
    /// </remarks>
    internal IReadOnlyList<Type> EndpointMetadataProviderTypes => _endpointMetadataProviderTypes;

    private bool tryApplyAsEndpointMetadataProvider(Type? type, RouteEndpointBuilder builder)
    {
        if (type != null && type.CanBeCastTo(typeof(IEndpointMetadataProvider)))
        {
            _endpointMetadataProviderTypes.Add(type);

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
        // GH-4749: only initialize when there is nothing to use yet. Each InitializeSynchronously
        // starts a fresh GeneratedAssembly that this already-assembled chain contributes no types to,
        // so re-running it is pure waste at best.
        if (_handlerType == null)
        {
            this.InitializeSynchronously(_parent.Rules, _parent, _parent.Container.Services);
        }

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
            DisplayName = DisplayName,
            ApplicationServices = RequiresApplicationServices
                ? _parent.Container.Services
                : EmptyServiceProvider.Instance // equivalent to not passing a value at all
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

            // Unlike the JSON branch below, a string endpoint has never advertised its missing-resource status
            // -- a null string used to throw out of HttpHandler.WriteString rather than answer anything at all.
            // Only document the status when the endpoint explicitly opted into one, so that fixing the null
            // string does not silently rewrite the OpenAPI of every string returning endpoint in the world.
            if (MissingResponseBodyStatusCode != 404)
            {
                Metadata.Produces(MissingResponseBodyStatusCode);
            }

            return;
        }

        Metadata.Produces(200, ResourceType, "application/json");

        // 404 unless this endpoint opted into an empty 204 for a null response body. Resolved by
        // ResolveMissingResponseBody() before this runs, and the same value the response writer emits,
        // so the generated OpenAPI cannot drift from what the endpoint actually returns.
        Metadata.Produces(MissingResponseBodyStatusCode);
    }

    internal interface IApplier
    {
        void Apply(EndpointBuilder builder, MethodInfo method);
    }

    /// <summary>
    ///     Invokes <see cref="IEndpointMetadataProvider.PopulateMetadata" /> — a static abstract interface
    ///     member, so a generic instantiation is the only way to call it at all.
    /// </summary>
    /// <remarks>
    ///     GH-4825. Public, not internal, because the emitted Native AOT rooting block names this closed
    ///     type inside a <c>typeof()</c> in generated code, and generated code cannot see an internal type.
    /// </remarks>
    public class Applier<T> : IApplier where T : IEndpointMetadataProvider
    {
        public void Apply(EndpointBuilder builder, MethodInfo method)
        {
            T.PopulateMetadata(method, builder);
        }
    }

    // Copied directly from `Microsoft.AspNetCore.Builder.EndpointBuilder`. Serves as the default
    // value of `RouteEndpointBuilder.ApplicationServices` when the endpoint does not require the
    // application's service provider.
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new EmptyServiceProvider();
        public object? GetService(Type serviceType) => null;
    }
}

internal class ProducesProblemDetailsResponseTypeMetadata : IProducesResponseTypeMetadata
{
    public Type? Type => typeof(ProblemDetails);
    public int StatusCode => 400;
    public IEnumerable<string> ContentTypes => new string[] {"application/problem+json" };
}