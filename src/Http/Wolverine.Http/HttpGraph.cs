using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Http.ContentNegotiation;
using Wolverine.Http.Resources;
using Wolverine.Runtime;
using Endpoint = Microsoft.AspNetCore.Http.Endpoint;

namespace Wolverine.Http;

public partial class HttpGraph : EndpointDataSource, ICodeFileCollectionWithServices, IChangeToken, IDescribeMyself
{
    public static readonly string Context = "httpContext";

    private readonly List<IResourceWriterPolicy> _builtInWriterPolicies =
    [
        new EmptyBody204Policy(),
        new StatusCodePolicy(),
        new ResultWriterPolicy(),
        new StringResourceWriterPolicy(),
        new ContentNegotiationWriterPolicy(),
        new JsonResourceWriterPolicy()
    ];

    private readonly List<HttpChain> _chains = [];
    private readonly List<RouteEndpoint> _endpoints = [];
    private readonly WolverineOptions _options;

    private readonly List<IResourceWriterPolicy> _optionsWriterPolicies = [];

    public HttpGraph(WolverineOptions options, IServiceContainer container)
    {
        _options = options;
        Container = container;
        Rules = _options.CodeGeneration;
    }

    internal IServiceContainer Container { get; }

    // CritterWatch #396 Phase 4 item 5: HTTP chains need the WolverineOptions to read
    // Tracking.EnableMessageCausationTracking when deciding whether to emit the endpoint-causation frame.
    internal WolverineOptions Options => _options;

    // Types registered via WolverineHttpOptions.SourceServiceFromHttpContext<T>().
    // Stored on the HTTP graph so the RequestServicesVariableSource is only added to
    // HTTP chains' per-method sources, never to the shared WolverineOptions.CodeGeneration.Sources
    // that non-HTTP message-handler chains also read from.
    internal HashSet<Type> HttpContextSourcedTypes { get; } = new();

    /// <summary>
    /// When true, automatically apply antiforgery metadata to form data and file upload endpoints.
    /// Defaults to false. Enable by calling <see cref="WolverineHttpOptions.AutoAntiforgeryOnFormEndpoints"/>.
    /// </summary>
    internal bool AutoAntiforgeryOnFormEndpoints { get; set; }

    internal IEnumerable<IResourceWriterPolicy> WriterPolicies => _optionsWriterPolicies.Concat(_builtInWriterPolicies);

    public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

    public IReadOnlyList<HttpChain> Chains => _chains;

    IDisposable IChangeToken.RegisterChangeCallback(Action<object?> callback, object? state)
    {
        return new StubDisposable();
    }

    bool IChangeToken.ActiveChangeCallbacks => false;

    bool IChangeToken.HasChanged => false;

    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        // Pre-generated endpoint registry for TypeLoadMode.Static cold-start (GH-2925, the Wolverine.Http
        // counterpart to the GH-2906 handler manifest): capture the discovered endpoint types so startup
        // can skip the HttpChainSource.FindActions ExportedTypes scan. The types come from the already-built
        // chains (chain.EndpointType), so no scan is needed to produce the manifest.
        var files = new List<ICodeFile>(_chains)
        {
            new HttpEndpointRegistryCodeFile(_chains.Select(x => x.EndpointType))
        };

        return files;
    }

    public string ChildNamespace => "WolverineHandlers";
    
    [IgnoreDescription]
    public GenerationRules Rules { get; }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription(this);

        var list = description.AddChildSet("Endpoints");
        list.SummaryColumns = ["Route", "Endpoint", "HttpMethods"];

        foreach (var chain in _chains)
        {
            var chainDescription = OptionsDescription.For(chain);
            chainDescription.Title = (chain.RoutePattern?.RawText)!;
            list.Rows.Add(chainDescription);
        }

        return description;
    }

    public void DiscoverEndpoints(WolverineHttpOptions wolverineHttpOptions)
    {
        var source = new HttpChainSource(_options.Assemblies);
        var logger = Container.GetInstance<ILogger<HttpGraph>>();

        // Cold-start fast path (GH-2925): in TypeLoadMode.Static, consume the pre-generated
        // HttpEndpointRegistry instead of scanning assemblies. Never applies during `codegen write`
        // itself — that must run a fresh scan to regenerate the registry accurately.
        MethodCall[] calls;
        if (!DynamicCodeBuilder.WithinCodegenCommand && Rules.TypeLoadMode == TypeLoadMode.Static &&
            HttpEndpointRegistry.TryLoad(_options.ApplicationAssembly, out var endpointTypes))
        {
            logger.LogInformation(
                "Using pre-generated Wolverine HTTP endpoint registry ({Count} endpoint types); skipping assembly scan",
                endpointTypes.Count);
            calls = source.FindActions(endpointTypes);
        }
        else
        {
            calls = source.FindActions();
        }

        logger.LogInformation("Found {Count} Wolverine HTTP endpoints in assemblies {Assemblies}", calls.Length,
            _options.Assemblies.Select(x => x.GetName().Name!).Join(", "));
        if (calls.Length == 0)
        {
            logger.LogWarning(
                "Found no Wolverine HTTP endpoints. If this is not expected, check the assemblies being scanned. See https://wolverine.netlify.app/guide/http/integration.html#discovery for more information");
        }

        _chains.AddRange(calls.Select(x => new HttpChain(x, this){ServiceProviderSource = wolverineHttpOptions.ServiceProviderSource}));

        // Expand multi-version handlers before any policy runs, so middleware, route prefix,
        // and other policies are applied uniformly to every per-version clone. Without this,
        // clones would miss whatever the policies subsequently mutate.
        if (wolverineHttpOptions.ApiVersioning is not null)
        {
            ApiVersioning.MultiVersionExpansion.ExpandInPlace(_chains);
        }

        wolverineHttpOptions.Middleware.Apply(_chains, Rules, Container);
        _optionsWriterPolicies.AddRange(wolverineHttpOptions.ResourceWriterPolicies);

        // Apply route prefix policy before other policies so that
        // downstream policies see the final route patterns
        var routePrefixPolicy = new RoutePrefixPolicy(wolverineHttpOptions);
        routePrefixPolicy.Apply(_chains, Rules, Container);

        var policies = _options.Policies.OfType<IChainPolicy>();
        foreach (var policy in policies) policy.Apply(_chains, Rules, Container);

        foreach (var policy in wolverineHttpOptions.Policies) policy.Apply(_chains, Rules, Container);

        _endpoints.AddRange(_chains.Select(x => x.BuildEndpoint(wolverineHttpOptions.WarmUpRoutes)));
    }

    public override IChangeToken GetChangeToken()
    {
        return this;
    }

    public HttpChain? ChainFor(string httpMethod, [StringSyntax("Route")] string urlPattern)
    {
        return _chains.FirstOrDefault(x => x.HttpMethods.Contains(httpMethod) && x.RoutePattern!.RawText == urlPattern);
    }

    public HttpChain Add(MethodCall method, HttpMethod httpMethod, string url)
    {
        var chain = new HttpChain(method, this);
        chain.MapToRoute(httpMethod.ToString(), url);
        _chains.Add(chain);
        return chain;
    }

    internal INewtonsoftHttpCodeGen? NewtonsoftCodeGen { get; private set; }

    /// <summary>
    ///     Wire the Newtonsoft.Json HTTP codegen path. Called by the WolverineFx.Http.Newtonsoft
    ///     extension package's <c>UseNewtonsoftJsonForSerialization()</c> extension method;
    ///     core <see cref="JsonResourceWriterPolicy"/> / <see cref="JsonBodyParameterStrategy"/>
    ///     dispatch through the supplied hook when <see cref="JsonUsage.NewtonsoftJson"/>
    ///     is selected.
    /// </summary>
    internal void UseNewtonsoftJson(INewtonsoftHttpCodeGen codeGen)
    {
        NewtonsoftCodeGen = codeGen ?? throw new ArgumentNullException(nameof(codeGen));

        var writerPolicy = _builtInWriterPolicies.OfType<JsonResourceWriterPolicy>().Single();
        writerPolicy.Usage = JsonUsage.NewtonsoftJson;
        writerPolicy.NewtonsoftCodeGen = codeGen;

        var bodyStrategy = _strategies.OfType<JsonBodyParameterStrategy>().Single();
        bodyStrategy.Usage = JsonUsage.NewtonsoftJson;
        bodyStrategy.NewtonsoftCodeGen = codeGen;
    }
}