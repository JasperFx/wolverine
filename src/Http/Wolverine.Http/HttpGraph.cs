using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Http.ContentNegotiation;
using Wolverine.Http.Resources;
using Wolverine.Http.Runtime;
using Wolverine.Runtime;
using Endpoint = Microsoft.AspNetCore.Http.Endpoint;

using Wolverine.Persistence;

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

        // Added here rather than in the _strategies initializer because it needs Rules, which is only assigned
        // above. Positioned relative to RouteParameterStrategy instead of at a fixed index so that inserting
        // anything else into the list later cannot silently change which strategy wins for a `now` parameter.
        var afterRouteArguments = _strategies.FindIndex(x => x is RouteParameterStrategy) + 1;
        _strategies.Insert(afterRouteArguments, new CurrentTimeParameterStrategy(Rules));
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

    /// <summary>
    /// When true, generated query string binding emits a 400 + ProblemDetails short circuit
    /// for query string values that are present but unparseable. Mirrors
    /// <see cref="WolverineHttpOptions.RejectUnparseableQueryValues"/>; transferred before
    /// endpoint discovery so chain construction sees the final value. GH-3372.
    /// </summary>
    internal bool RejectUnparseableQueryValues { get; set; }

    private int _warnedAboutLenientQueryBinding;

    /// <summary>
    /// GH-4529. Called from <see cref="HttpChain"/> the first time an endpoint binds a <b>parsed</b>
    /// (non-string) query string parameter while <see cref="RejectUnparseableQueryValues"/> is off -- that is,
    /// the first time this application actually acquires the surprising behaviour rather than merely being
    /// configured for it.
    ///
    /// <para>
    /// Under the default, a query string value that is present but unparseable binds the parameter's default
    /// and the request proceeds: <c>?page=abc</c> on an <c>int page</c> runs with <c>page = 0</c>. Nothing is
    /// logged, the server sees a valid request, and every "the API returned the wrong page" investigation
    /// starts from a clean access log. MVC and minimal APIs both reject it.
    /// </para>
    ///
    /// <para>
    /// Warned exactly once per graph, not once per endpoint, and only when a parsed parameter exists --
    /// an application with only string query parameters has nothing to act on. Raised from the binding site
    /// rather than after discovery because chain compilation is lazy under
    /// <see cref="RouteWarmup.Lazy"/>, so a scan at startup would not yet know.
    /// </para>
    /// </summary>
    internal void WarnOnceAboutLenientQueryBinding()
    {
        if (RejectUnparseableQueryValues) return;
        if (Interlocked.Exchange(ref _warnedAboutLenientQueryBinding, 1) == 1) return;

        // Chain compilation also runs in contexts with a bare container -- the query string
        // troubleshooting helpers, codegen -- where no logging has been registered at all. A diagnostic
        // must never be the thing that takes those down.
        ILogger logger;
        try
        {
            logger = Container.GetInstance<ILogger<HttpGraph>>();
        }
        catch (Exception)
        {
            return;
        }

        logger.LogWarning(
            "WolverineHttpOptions.RejectUnparseableQueryValues is false, so a query string value that is present but cannot be parsed binds the parameter's default value and the request proceeds -- '?page=abc' on an int parameter runs with page = 0, and nothing is logged. Set WolverineHttpOptions.RejectUnparseableQueryValues = true to answer 400 with ProblemDetails instead, which is what MVC and minimal APIs do. This default flips to true in Wolverine 7.");
    }

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
        // GH-4841. The rooting block below is only emitted under `codegen write`, and one of its inputs --
        // the IEndpointMetadataProvider types -- is RECORDED by HttpChain.BuildEndpoint rather than
        // recomputed here. That is deliberate (see HttpChain.EndpointMetadataProviderTypes), and it rests
        // on DiscoverEndpoints having built every endpoint before this runs. Nothing else enforces that
        // order, and the failure if it were ever broken is a rooting block that is silently short, which
        // a native image reports as a startup crash and nothing reports sooner. So: refuse, by name.
        if (DynamicCodeBuilder.WithinCodegenCommand)
        {
            assertEveryEndpointWasBuiltBeforeCodegen();
        }

        // Pre-generated endpoint registry for TypeLoadMode.Static cold-start (GH-2925, the Wolverine.Http
        // counterpart to the GH-2906 handler manifest): capture the discovered endpoint types so startup
        // can skip the HttpChainSource.FindActions ExportedTypes scan. The types come from the already-built
        // chains (chain.EndpointType), so no scan is needed to produce the manifest.
        // GH-4778. The response-aware return types, for the rooting block: HttpChain's own
        // tryApplyResponseAware closes Applier<T> over each of them at startup -- which is the reported
        // crash, since an [WriteAggregate] endpoint returning Marten's UpdatedAggregate lands here.
        var responseAwareTypes = _chains
            .SelectMany(x => x.ReturnVariablesOfType(typeof(IResponseAware)))
            .Select(x => x.VariableType);

        // GH-4778. The GENERATED endpoint types, which are what AssertPreBuiltTypesExist looks up by name
        // and what Static mode actually executes. They were unrooted too: the first native image built for
        // this threw MissingPreBuiltTypesException naming POST_aot_response_aware, before it ever reached
        // the Applier<T> close. The handler registry has always rooted its generated handler types; this
        // is the counterpart it never had.
        var generatedEndpointTypeNames = _chains.Select(x => ((ICodeFile)x).FileName);

        // GH-4765. The counterpart collection to the handler graph's: closed types named by the frames
        // themselves, which is the only way a frame belonging to a persistence package can be rooted at
        // all. EF Core's CreateTenantedDbContext<T> on an endpoint that takes a multi-tenanted DbContext is
        // the live case.
        //
        // NOT the Wolverine.Http.Marten compiled-query postprocessors, despite the obvious resemblance --
        // CompiledQueryWriterPolicy closes those from HttpChain.DetermineFrames, which only
        // ICodeFile.AssembleTypes calls, and BuildFiles materializes this whole list (and with it the
        // registry's captured roots) BEFORE any AssembleTypes runs. They are also unreachable in a native
        // image for the same reason: StaticTypeLoader attaches the pre-generated type and never assembles.
        // A frame has to be placed by a POLICY, at startup, to need rooting and to be collectable here.
        var contributedRootTypes = _chains
            .SelectMany(x => x.Middleware.Concat(x.Postprocessors).Concat(x.PostCommitPostprocessors))
            .OfType<IAotRootSource>()
            .SelectMany(x => x.AotRoots());

        // GH-4825. The two shapes a Marten-backed HTTP app reaches that nothing had rooted:
        //
        //   - The ISideEffect return types, whose public Execute/ExecuteAsync SideEffectPolicy looks up
        //     reflectively while the chains are built. An endpoint returning MartenOps.StartStream threw
        //     InvalidSideEffectException naming IStartStream before the host started.
        //   - The IEndpointMetadataProvider types HttpChain closed its OWN Applier<T> over. That is a
        //     different Applier<T> from the Wolverine.Configuration one GH-4778 roots, and
        //     Results<Ok<T>, ProblemHttpResult> -- a plain minimal-API return type -- lands on it.
        //     Recorded by BuildEndpoint as the closes happen, which is what the guard at the top of this
        //     method protects.
        var sideEffectTypes = _chains.SelectMany(SideEffectAotRoots.Of);
        var metadataProviderTypes = _chains.SelectMany(x => x.EndpointMetadataProviderTypes);

        var files = new List<ICodeFile>(_chains)
        {
            new HttpEndpointRegistryCodeFile(_chains.Select(x => x.EndpointType), responseAwareTypes,
                generatedEndpointTypeNames, contributedRootTypes, sideEffectTypes, metadataProviderTypes)
        };

        return files;
    }

    /// <summary>
    ///     GH-4841. <c>codegen write</c> must not emit the HTTP rooting block for a chain whose endpoint was
    ///     never built, because the <see cref="IEndpointMetadataProvider" /> roots in that block are
    ///     recorded by <see cref="HttpChain.BuildEndpoint" /> and would be missing without a trace.
    /// </summary>
    /// <remarks>
    ///     Only under <see cref="DynamicCodeBuilder.WithinCodegenCommand" />: <see cref="BuildFiles" /> is
    ///     also enumerated by <c>AssertPreBuiltTypesExist</c> during <see cref="TypeLoadMode.Static" />
    ///     attach, which runs BEFORE the endpoints are built and emits nothing.
    /// </remarks>
    private void assertEveryEndpointWasBuiltBeforeCodegen()
    {
        var unbuilt = _chains.Where(x => x.Endpoint == null).ToArray();
        if (unbuilt.Length == 0) return;

        throw new InvalidOperationException(
            $"Code generation reached {nameof(HttpGraph)}.{nameof(BuildFiles)} before {nameof(DiscoverEndpoints)} had built the endpoint for {unbuilt.Length} of {_chains.Count} HTTP chain(s): {string.Join(", ", unbuilt.Select(x => x.ToString()))}. " +
            "The IEndpointMetadataProvider roots in the generated HttpAotRoots block are recorded while each endpoint is built, so generating now would emit a rooting block that is silently missing them and a Native AOT image that fails at startup. " +
            "Call MapWolverineEndpoints() on the application before handing it to RunJasperFxCommands().");
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

    /// <summary>
    ///     The application-wide default duplicate status code, read by each HttpChain while it is being
    ///     constructed. It has to be available that early: registerDeduplicationMetadata() runs inside the
    ///     chain constructor, well before IHttpPolicy instances get a look, so a policy-applied default
    ///     would set the runtime status while OpenAPI still advertised 409.
    /// </summary>
    internal int DefaultDuplicateStatusCode { get; private set; } =
        DeduplicationRequirement.DefaultDuplicateStatusCode;

    public void DiscoverEndpoints(WolverineHttpOptions wolverineHttpOptions)
    {
        DefaultDuplicateStatusCode = wolverineHttpOptions.DefaultDuplicateStatusCode;

        var source = new HttpChainSource(_options.Assemblies, wolverineHttpOptions.EndpointDiscovery);
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

        // Two different routes can sanitize to the same generated C# type name (e.g. "/a$b" and "/a-b"
        // both -> "a_b"). Append a deterministic suffix to any that actually collide so codegen stays
        // valid. See GH-3282.
        ResolveDuplicateTypeNames(_chains);

        // Expand multi-version handlers before any policy runs, so middleware, route prefix,
        // and other policies are applied uniformly to every per-version clone. Without this,
        // clones would miss whatever the policies subsequently mutate.
        if (wolverineHttpOptions.ApiVersioning is not null)
        {
            ApiVersioning.MultiVersionExpansion.ExpandInPlace(_chains);
        }

        wolverineHttpOptions.Middleware.Apply(_chains, Rules, Container);
        _optionsWriterPolicies.AddRange(wolverineHttpOptions.ResourceWriterPolicies);

        // After the API versioning expansion above so the per-version clones are covered too, and before
        // BuildEndpoint() below, which bakes the resolved status code into the endpoint's OpenAPI metadata.
        foreach (var chain in _chains) chain.ResolveMissingResponseBody(wolverineHttpOptions);

        // Apply route prefix policy before other policies so that
        // downstream policies see the final route patterns
        var routePrefixPolicy = new RoutePrefixPolicy(wolverineHttpOptions);
        routePrefixPolicy.Apply(_chains, Rules, Container);

        var policies = _options.Policies.OfType<IChainPolicy>();
        foreach (var policy in policies) policy.Apply(_chains, Rules, Container);

        foreach (var policy in wolverineHttpOptions.Policies) policy.Apply(_chains, Rules, Container);

        // GH-4156. BEFORE BuildEndpoint, deliberately. In TypeLoadMode.Static BuildEndpoint already forces
        // the handler build for every chain -- regardless of RouteWarmup -- so a missing pre-built type does
        // already fail the mapping rather than the first request. What it fails with is the problem: JasperFx
        // throws ExpectedTypeMissingException on the FIRST chain it reaches, naming one generated code file
        // and the assembly it looked in, and nothing else. That leaves the operator without the count, the
        // routes in a form they recognize, or the one fact that actually resolves this -- that the types are
        // sitting in the entry assembly instead. Assert here so the whole picture is reported at once.
        AssertPreBuiltTypesExist();

        // GH-4742. A requirement a policy set is validated here, at startup, and its refusal codes described.
        foreach (var chain in _chains) chain.FinalizeDeduplicatedWithResponse();

        warnAboutDeduplicatedResponsesWithoutStorage(logger);

        _endpoints.AddRange(_chains.Select(x => x.BuildEndpoint(wolverineHttpOptions.WarmUpRoutes)));

        // After BuildEndpoint: the authorization metadata this reads only exists on the built endpoint.
        warnAboutAnonymousUserScopedDeduplication(logger);
    }

    /// <summary>
    /// GH-4742. <see cref="DeduplicationScope.None" /> is refused outright, because a stored response that is
    /// scoped by nothing can be replayed to any caller who presents the same key and request bytes. An
    /// UNAUTHENTICATED caller resolves the user component of the key to <c>string.Empty</c>, so
    /// <see cref="DeduplicationScope.User" /> on an anonymous endpoint produces exactly the key
    /// <c>None</c> would -- the same hole, reached by a route the refusal does not cover.
    /// <para>A warning rather than a refusal: an endpoint fronted by a gateway that has already
    /// authenticated the caller, or authorized by a convention applied outside this graph, is legitimate and
    /// must still start.</para>
    /// </summary>
    private void warnAboutAnonymousUserScopedDeduplication(ILogger logger)
    {
        var hasFallbackPolicy = hasFallbackAuthorizationPolicy();

        foreach (var chain in _chains)
        {
            if (chain.DeduplicatedWithResponse is not { } requirement) continue;
            if (!requirement.Scope.HasFlag(DeduplicationScope.User)) continue;

            var metadata = chain.Endpoint?.Metadata;
            if (metadata == null) continue;

            // [AllowAnonymous] wins over [Authorize] in ASP.NET Core, so an endpoint carrying both is
            // anonymous and belongs in this warning. It wins over a fallback policy too.
            var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
            var authorized = (metadata.OfType<IAuthorizeData>().Any() || hasFallbackPolicy) && !allowsAnonymous;
            if (authorized) continue;

            var route = chain.RoutePattern?.RawText ?? chain.Description;

            // Two reasons reach this point and they want different advice, so they get their own messages:
            // an endpoint nothing authorizes wants authorization added, while one that opted out of a
            // fallback policy with [AllowAnonymous] is already configured the way its author intended and
            // wants the attribute or the scope reconsidered instead. Saying "there is no fallback
            // authorization policy" in both cases was false in the second one, where there is one.
            if (allowsAnonymous)
            {
                logger.LogWarning(
                    "[DeduplicatedWithResponse] on {Route} scopes by DeduplicationScope.User, but the endpoint is marked [AllowAnonymous], so no authorization applies to it -- a fallback policy included. An unauthenticated caller scopes by nothing, so the stored response could be replayed to any caller presenting the same idempotency key and request bytes. Remove [AllowAnonymous], change the scope, or ignore this if the caller is already authenticated upstream. See GH-4742",
                    route);
            }
            else
            {
                logger.LogWarning(
                    "[DeduplicatedWithResponse] on {Route} scopes by DeduplicationScope.User, but the endpoint carries no authorization metadata and no fallback authorization policy is configured. An unauthenticated caller scopes by nothing, so the stored response could be replayed to any caller presenting the same idempotency key and request bytes. Require authorization on the endpoint, configure AuthorizationOptions.FallbackPolicy, or ignore this if the caller is already authenticated upstream. See GH-4742",
                    route);
            }
        }
    }

    // ASP.NET Core authorizes every endpoint with no authorization metadata of its own by
    // AuthorizationOptions.FallbackPolicy, so an application that authorizes that way declares nothing per endpoint.
    private bool hasFallbackAuthorizationPolicy()
    {
        try
        {
            return Container.GetInstance<IOptions<AuthorizationOptions>>().Value.FallbackPolicy != null;
        }
        catch (Exception)
        {
            // A diagnostic must never be what takes startup down.
            return false;
        }
    }

    /// <summary>
    /// GH-4742. Warn rather than fail, as [Deduplicated] does: the endpoint may live in an assembly shared with
    /// a host that never serves it. The endpoint itself throws at its first claim.
    /// </summary>
    private void warnAboutDeduplicatedResponsesWithoutStorage(ILogger logger)
    {
        // By the store each chain claims in: the main one, or its ancillary store.
        var byStore = _chains.Where(x => x.DeduplicatedWithResponse != null)
            .GroupBy(x => x.AncillaryStoreType)
            .ToArray();

        if (byStore.Length == 0) return;

        IWolverineRuntime runtime;
        try
        {
            runtime = Container.GetInstance<IWolverineRuntime>();
        }
        catch (Exception)
        {
            // A diagnostic must never be what takes startup down.
            return;
        }

        foreach (var chains in byStore)
        {
            string? reason;
            try
            {
                reason = DeduplicatedResponses.WhyUnsupported(runtime, chains.Key);
            }
            catch (Exception)
            {
                continue;
            }

            if (reason == null) continue;

            logger.LogWarning(
                "[DeduplicatedWithResponse] is used by {Routes}, but {Reason}, so those endpoints will fail at their first request. See GH-4742",
                chains.Select(x => x.RoutePattern?.RawText ?? x.Description).Join(", "), reason);
        }
    }

    internal static void ResolveDuplicateTypeNames(IReadOnlyList<HttpChain> chains)
    {
        foreach (var group in chains.GroupBy(x => x.Description).Where(g => g.Count() > 1))
        {
            foreach (var chain in group)
            {
                chain.DisambiguateTypeName(deterministicSuffix(chain));
            }
        }
    }

    // A stable (process-independent) FNV-1a hash of what actually distinguishes two colliding chains,
    // so the generated type names stay deterministic across builds — important for TypeLoadMode.Static
    // where the codegen-time names must match what was written to disk.
    private static string deterministicSuffix(HttpChain chain)
    {
        var key = $"{chain.Method.HandlerType.FullName}.{chain.Method.Method.Name}|{chain.HttpMethods.Join(",")}|{chain.RoutePattern?.RawText}";

        uint hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(key))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash.ToString("x8");
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
        // GH-3646: the route goes through the constructor rather than being mapped onto the finished chain,
        // so the parameter strategies run before applyMetadata() reads what they assign.
        var chain = new HttpChain(method, this, httpMethod.ToString(), url);
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