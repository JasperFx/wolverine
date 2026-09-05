using Asp.Versioning;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wolverine.Configuration;
using Wolverine.Http.Antiforgery;
using Wolverine.Http.CodeGen;
using Wolverine.Http.ContentNegotiation;
using Wolverine.Http.Metadata;
using Wolverine.Http.Policies;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using ServiceContainer = JasperFx.ServiceContainer;

namespace Wolverine.Http;

public partial class HttpChain : Chain<HttpChain, ModifyHttpChainAttribute>, ICodeFile, IEndpointNameMetadata, IEndpointSummaryMetadata, IEndpointDescriptionMetadata, IDescribeMyself, IRoutedChain
{
    public static bool IsValidResponseType(Type type)
    {
        if (type == typeof(IEnumerable<object>) || type == typeof(object[]))
        {
            return false;
        }

        if (type.CanBeCastTo<IWolverineReturnType>())
        {
            return false;
        }

        if (type.CanBeCastTo<IAsyncEnumerable<object>>())
        {
            return false;
        }

        return true;
    }

    public static readonly Variable[] HttpContextVariables =
        Variable.VariablesForProperties<HttpContext>(HttpGraph.Context);

    // GH-4171: HttpContext.RequestServices is one of the properties above, and derived variables win
    // over every variable source during arrangement -- so an endpoint (or middleware) asking for
    // IServiceProvider silently got httpContext.RequestServices no matter what ServiceProviderSource
    // said, and never registered as a service location at all. Leave IServiceProvider out of the
    // derived set and let the normal service-variable machinery answer it: IsolatedAndScoped gets
    // Wolverine's own child scope, FromHttpContextRequestServices gets httpContext.RequestServices
    // through TryReplaceServiceProvider, and both are reported to ServiceLocationPolicy.
    internal static readonly Variable[] DerivedHttpContextVariables =
        HttpContextVariables.Where(x => x.VariableType != typeof(IServiceProvider)).ToArray();

    // Used by CloneForVersion to sanitize ApiVersion text (e.g. "2024-01-01") into a legal
    // identifier suffix for OperationId. Compiled once; only ASCII alphanumerics survive.
    private static readonly Regex NonAlphanumeric = new(@"[^A-Za-z0-9]", RegexOptions.Compiled);

    internal Variable? RequestBodyVariable { get; set; }

    /// <summary>
    /// True when the request body is optional — i.e. a nullable [FromBody] member inside an
    /// [AsParameters] type. Drives both the runtime read (an empty body binds null instead of 400)
    /// and the generated OpenAPI (requestBody.required = false). See GH-3135.
    /// </summary>
    internal bool RequestBodyIsOptional { get; set; }

    private string? _fileName;
    private string? _typeNameOverride;
    private readonly List<string> _httpMethods = [];

    private readonly List<Variable> _routeVariables = [];

    private readonly HttpGraph _parent;

    private readonly List<HttpElementVariable> _querystringVariables = [];

    private readonly List<HttpElementVariable> _formValueVariables = [];

    public string OperationId { get; set; }
    public bool HasExplicitOperationId { get; private set; }
    public string? EndpointSummary { get; set; }
    public string? EndpointDescription { get; set; }

    /// <summary>
    /// This may be overridden by some IResponseAware policies in place of the first
    /// create variable of the method call
    /// </summary>
    [IgnoreDescription]
    public Variable? ResourceVariable { get; set; }

    /// <summary>
    /// Controls how content negotiation behaves when no matching content type writer is found.
    /// Default is Loose (falls back to JSON). Set to Strict to return 406 Not Acceptable.
    /// </summary>
    public ConnegMode ConnegMode { get; set; } = ConnegMode.Loose;

    // Make the assumption that the route argument has to match the parameter name
    private GeneratedType? _generatedType;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private Type? _handlerType;
    private string _description;
    private Type? _requestType;

    public HttpChain(MethodCall method, HttpGraph parent) : this(method, parent, null, null)
    {
    }

    /// <summary>
    ///     GH-3646: routes supplied explicitly rather than by a <see cref="WolverineHttpMethodAttribute" /> —
    ///     <see cref="HttpGraph.Add" />, and through it <c>PublishMessage&lt;T&gt;</c> / <c>SendMessage&lt;T&gt;</c> —
    ///     have to be mapped from INSIDE the constructor, not bolted on after it. <see cref="MapToRoute" /> is what
    ///     runs the parameter strategies that assign <see cref="RequestType" />, <see cref="IsFormData" /> and the
    ///     HTTP methods, and <see cref="applyMetadata" /> is the constructor's last statement. Mapping the route
    ///     afterwards left metadata built from an unassigned request type: the endpoint advertised no
    ///     <c>IAcceptsMetadata</c> at all and carried an empty <c>HttpMethodMetadata</c>, even though the chain
    ///     itself knew the request type perfectly well.
    /// </summary>
    internal HttpChain(MethodCall method, HttpGraph parent, string? httpMethod, string? url)
    {
        _description = method.ToString();
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Method.CommentText = "The actual HTTP request handler execution";

        DisplayName = Method.ToString();

        if (tryFindResourceType(method, out var responseType))
        {
            NoContent = false;
            ResourceType = responseType;
        }
        else
        {
            NoContent = true;
            ResourceType = typeof(void);
        }

        Metadata = new RouteHandlerBuilder(new[] { this });

        if (method.Method.TryGetAttribute<WolverineHttpMethodAttribute>(out var att))
        {
            _typeNameOverride = att.TypeName;
            MapToRoute(att.HttpMethod, att.Template, att.Order);
            if (att.Name.IsNotEmpty())
            {
                DisplayName = att.Name;
            }

            if (att.RouteName.IsNotEmpty())
            {
                RouteName = att.RouteName;
            }

            if (att.OperationId.IsNotEmpty())
            {
                OperationId = att.OperationId;
                HasExplicitOperationId = true;
            }

            if (att.Summary.IsNotEmpty())
            {
                EndpointSummary = att.Summary;
            }

            if (att.Description.IsNotEmpty())
            {
                EndpointDescription = att.Description;
            }
        }

        // Applied after the attribute so an explicit route still wins, exactly as it did when HttpGraph.Add
        // called MapToRoute() on the constructed chain. See GH-3646.
        if (httpMethod != null && url != null)
        {
            MapToRoute(httpMethod, url);
        }

        OperationId ??= $"{Method.HandlerType.FullNameInCode()}.{Method.Method.Name}";

        // Apply attributes and the Configure() method if that exists too
        applyAttributesAndConfigureMethods(_parent.Rules, _parent.Container);

        // Add Before/After methods from the current handler
        ApplyImpliedMiddlewareFromHandlers(_parent.Rules);

        foreach (var call in Middleware.OfType<MethodCall>().ToArray())
        {
            parent.ApplyParameterMatching(this, call);
        }

        // GH-4180. Register the deduplication refusal codes BEFORE applyMetadata() runs.
        //
        // The frames themselves cannot do this. They are woven in AssembleTypes, which is lazy and runs
        // long after the endpoint metadata has been built -- so a Produces() call from there lands on an
        // object nobody reads again, and the 409 a client can actually receive never appears in the
        // generated OpenAPI document. The status codes are known from the requirement alone, so they do
        // not need to wait for weaving.
        registerDeduplicationMetadata();

        applyMetadata();
    }

    /// <summary>
    ///     The duplicate status code this endpoint actually answers with: whatever
    ///     <c>[Deduplicated]</c> asked for, or the application-wide
    ///     <see cref="WolverineHttpOptions.DefaultDuplicateStatusCode" /> when it asked for nothing.
    ///     Deliberately keyed off whether a code was stated rather than off its value, so an endpoint
    ///     that explicitly wants 409 keeps 409 even under an application default of something else.
    /// </summary>
    private int effectiveDuplicateStatusCode(DeduplicationRequirement requirement)
        => requirement.ExplicitDuplicateStatusCode ?? _parent.DefaultDuplicateStatusCode;

    private void registerDeduplicationMetadata()
    {
        if (Deduplication is not { } requirement) return;

        if (requirement.Required)
        {
            // Produces<ProblemDetails>, not Produces(..., contentType:). Without a response TYPE the
            // content type never reaches the generated OpenAPI document -- Swashbuckle emits the status
            // with no content at all, so a client generating from the spec cannot see that a refusal
            // carries a problem document. Covered by deduplication_openapi_document.
            Metadata.Produces<ProblemDetails>(400, "application/problem+json");
        }

        var status = effectiveDuplicateStatusCode(requirement);

        if (status is >= 200 and < 300)
        {
            Metadata.Produces(status);
        }
        else
        {
            Metadata.Produces<ProblemDetails>(status, "application/problem+json");
        }
    }

    private bool tryFindResourceType(MethodCall method, out Type resourceType)
    {
        resourceType = typeof(void);

        if (!method.Creates.Any())
        {
            return false;
        }

        if (method.Method.HasAttribute<EmptyResponseAttribute>() ||
            method.HandlerType.HasAttribute<EmptyResponseAttribute>())
        {
            return false;
        }

        var responseBody = method.Creates.First();

        resourceType = responseBody.VariableType;
        return IsValidResponseType(resourceType);
    }

    public bool NoContent { get; }

    [IgnoreDescription]
    public MethodCall Method { get; }

    public Type EndpointType => Method.HandlerType;

    public string? RouteName { get; set; }

    [IgnoreDescription]
    public string? DisplayName { get; set; }
    
    public int Order { get; set; }

    [IgnoreDescription]
    public IEnumerable<string> HttpMethods => _httpMethods;

    public Type? ResourceType { get; private set; }

    internal void MapToRoute(string method, string url, int? order = null, string? displayName = null)
    {
        RoutePattern = RoutePatternFactory.Parse(url);
        _httpMethods.Fill(method);
        if (order != null)
        {
            Order = order.Value;
        }

        if (displayName.IsNotEmpty())
        {
            DisplayName = displayName;
        }

        _fileName = determineFileName();

        _description = _fileName;

        _parent.ApplyParameterMatching(this);

        // Doing this prevents middleware policies
        // from doing something stupid
        RequestType ??= typeof(void);
    }

    /// <summary>
    /// Append a deterministic suffix to the generated C# type name so two routes that would otherwise
    /// derive the same identifier (e.g. "/a$b" and "/a-b" both sanitizing to "a_b") stay unique. Called
    /// by <see cref="HttpGraph"/> only for chains whose generated name actually collides.
    /// </summary>
    internal void DisambiguateTypeName(string suffix)
    {
        _fileName = $"{_fileName}_{suffix}";
        _description = _fileName;
    }

    [IgnoreDescription]
    public RoutePattern? RoutePattern { get; internal set; }

    public Type? RequestType
    {
        get => _requestType;
        internal set
        {
            _requestType = value;
            if (_requestType != null)
            {
                applyAuditAttributes(_requestType);
            }
        }
    }

    public override string Description => _description;

    internal RouteEndpoint? Endpoint { get; private set; }

    /// <summary>
    /// Required TenancyMode for this http chain
    /// </summary>
    public TenancyMode? TenancyMode { get; set; }

    /// <summary>API version declared for this endpoint via [ApiVersion] or fluent configuration. Null when the endpoint is version-neutral.</summary>
    public ApiVersion? ApiVersion { get; set; }

    /// <summary>
    /// True when this endpoint has been explicitly marked version-neutral via
    /// <see cref="Asp.Versioning.ApiVersionNeutralAttribute"/>. Neutral chains keep their declared
    /// route, are skipped by version-aware route rewriting, duplicate detection on the version axis,
    /// and response-header emission, and satisfy <see cref="ApiVersioning.UnversionedPolicy.RequireExplicit"/>.
    /// </summary>
    public bool IsApiVersionNeutral { get; set; }

    /// <summary>Sunset policy for this endpoint's API version. Populated by configuration during app startup.</summary>
    public SunsetPolicy? SunsetPolicy { get; set; }

    /// <summary>Deprecation policy for this endpoint's API version. Populated by configuration during app startup.</summary>
    public DeprecationPolicy? DeprecationPolicy { get; set; }

    /// <summary>Fluent helper to declare an API version on this chain. Returns this chain.</summary>
    public HttpChain HasApiVersion(ApiVersion version)
    {
        ApiVersion = version;
        return this;
    }

    /// <summary>
    /// Builds a fresh <see cref="HttpChain"/> from the same handler method so it can serve a
    /// distinct API version. The clone re-runs the standard ctor pipeline (attributes, configure
    /// methods, parameter matching, implied middleware), so attribute-driven policies — auth,
    /// fluent validation, before/after middleware, cascading messages — are reapplied per version.
    /// The clone's <see cref="ApiVersion"/> is set to <paramref name="version"/> and its
    /// <see cref="DeprecationPolicy"/> is set when <paramref name="isDeprecated"/> is true.
    /// </summary>
    /// <remarks>
    /// Used by multi-version expansion at bootstrap. Expansion runs before any policy in the
    /// HTTP pipeline so middleware, route prefix, and downstream policies are applied to clones
    /// uniformly with the source chain.
    /// </remarks>
    internal HttpChain CloneForVersion(ApiVersion version, bool isDeprecated)
    {
        // Each clone needs its own MethodCall so JasperFx codegen can wire each handler frame
        // independently. Re-using the source MethodCall makes the second clone's codegen throw
        // "Frame chain is being re-arranged" when JasperFx tries to set Next on a frame that's
        // already chained from the first clone.
        var clonedMethodCall = new MethodCall(Method.HandlerType, Method.Method);
        var clone = new HttpChain(clonedMethodCall, _parent)
        {
            ServiceProviderSource = ServiceProviderSource,
            ApiVersion = version
        };

        // Multi-version expansion produces N chains sharing the same handler method, so the
        // ctor-derived OperationId collides across clones. Suffix it with the version to keep
        // ASP.NET Core's "endpoint names must be globally unique" invariant intact.
        // Sanitize by replacing every non-alphanumeric character so date-based versions like
        // 2024-01-01 still produce a legal identifier (2024_01_01) instead of leaking hyphens.
        var versionSuffix = NonAlphanumeric.Replace(version.ToString(), "_");
        clone.OperationId = $"{clone.OperationId}_v{versionSuffix}";

        if (isDeprecated)
        {
            clone.DeprecationPolicy ??= new DeprecationPolicy();
        }

        // Strip [ApiVersion] / [MapToApiVersion] attributes that don't match this clone's version.
        // applyMetadata() copied every class- and method-level attribute onto the clone, so without
        // this pass each clone's ASP.NET Core endpoint metadata reports ALL of the multi-version
        // declarations and OpenAPI tooling reports each clone as implementing every sibling version.
        clone.Metadata.Add(builder =>
        {
            for (var i = builder.Metadata.Count - 1; i >= 0; i--)
            {
                var m = builder.Metadata[i];
                if (m is ApiVersionAttribute a && !a.Versions.Contains(version))
                    builder.Metadata.RemoveAt(i);
                else if (m is MapToApiVersionAttribute mp && !mp.Versions.Contains(version))
                    builder.Metadata.RemoveAt(i);
            }
        });

        return clone;
    }

    public static HttpChain ChainFor<T>(Expression<Action<T>> expression, HttpGraph? parent = null)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method!);

        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();

        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);

        var serviceContainer = registry.BuildServiceProvider();
        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), serviceContainer.GetRequiredService<IServiceContainer>()));
    }

    public static HttpChain ChainFor(Type handlerType, string methodName, HttpGraph? parent = null)
    {
        var call = new MethodCall(handlerType, methodName);

        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);

        var provider = registry.BuildServiceProvider();
        
        var serviceContainer = provider.GetRequiredService<IServiceContainer>();
        
        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), serviceContainer));
    }

    public bool HasResourceType()
    {
        return ResourceType != null && ResourceType != typeof(void) && ResourceType.FullName != "Microsoft.FSharp.Core.Unit";
    }

    public override bool TryInferMessageIdentity(out PropertyInfo? property)
    {
        var atts = Method.HandlerType.GetCustomAttributes()
            .Concat(Method.Method.GetCustomAttributes())
            .Concat(Method.Method.GetParameters().SelectMany(x => x.GetCustomAttributes()))
            .OfType<IMayInferMessageIdentity>().ToArray();

        foreach (var att in atts)
        {
            if (att.TryInferMessageIdentity(this, out property)) return true;
        }
        
        property = default;
        return false;
    }

    public override bool ShouldFlushOutgoingMessages()
    {
        return true;
    }

    public override MethodCall[] HandlerCalls()
    {
        return [Method];
    }

    public override bool HasAttribute<T>()
    {
        return Method.HandlerType.HasAttribute<T>() || Method.Method.HasAttribute<T>();
    }

    public override Type? InputType()
    {
        return HasRequestType ? RequestType : ComplexQueryStringType;
    }

    public override Frame? CreateSimpleValidationFrame(Variable variable)
    {
        Metadata.Produces(400, contentType: "application/problem+json");
        return new SimpleValidationHttpFrame(variable);
    }

    public override Frame? CreateRequirementResultFrame(Variable variable)
    {
        Metadata.Produces(400, contentType: "application/problem+json");
        return new RequirementResultHttpFrame(variable);
    }

    public override Frame[] AddStopConditionIfNull(Variable variable)
    {
        return [new SetStatusCodeAndReturnIfEntityIsNullFrame(variable)];
    }

    /// <inheritdoc />
    /// <remarks>
    ///     GH-4180. An HTTP endpoint owes its caller an answer, so a refused request gets a status code
    ///     rather than the silent discard a message handler gets. Both codes are registered as endpoint
    ///     metadata so they show up in OpenAPI — a 409 a client can receive but cannot discover from the
    ///     generated spec is a contract change hidden from exactly the people who need it.
    /// </remarks>
    public override Frame[] BuildDeduplicationStopCondition(Variable condition, DeduplicationOutcome outcome,
        DeduplicationRequirement requirement)
    {
        var key = requirement.Key ?? DeduplicationRequirement.DefaultHeaderName;

        if (outcome == DeduplicationOutcome.MissingId)
        {
            // Metadata is registered in registerDeduplicationMetadata() during construction, not here --
            // this runs during the lazy AssembleTypes pass, too late for the endpoint metadata build.
            return
            [
                new DeduplicationProblemDetailsFrame(condition, 400,
                    $"This endpoint requires a logical deduplication id in the '{key}' header")
            ];
        }

        var status = effectiveDuplicateStatusCode(requirement);

        // A success code means the application has declared a replayed request benign, so there is
        // nothing to explain and a problem document would be actively wrong. Anything else is a
        // refusal, and a refusal without a reason is a status code the caller has to guess at.
        if (status is >= 200 and < 300)
        {
            return [new DeduplicationStatusCodeFrame(condition, status)];
        }

        return
        [
            new DeduplicationProblemDetailsFrame(condition, status,
                $"A request with this '{key}' has already been handled")
        ];
    }

    public override Frame[] AddStopConditionIfNull(Variable data, Variable? identity, IDataRequirement requirement)
    {
        // AddStopConditionIfNull declares the identity nullable, so an entity addressed by
        // something other than a single identity variable has none for the stock message to name.
        var message = requirement.MissingMessage ?? (identity == null
            ? $"Required {data.VariableType.NameInCode()} was not found"
            : $"Unknown {data.VariableType.NameInCode()} with identity {{Id}}");
        
        switch (requirement.OnMissing)
        {
            case OnMissing.Simple404:
                Metadata.Produces(404);
                return [new SetStatusCodeAndReturnIfEntityIsNullFrame(data)];
                
            case OnMissing.ProblemDetailsWith400:
                Metadata.Produces(400, contentType: "application/problem+json");
                return [new WriteProblemDetailsIfNull(data, identity, message, 400)];
            case OnMissing.ProblemDetailsWith404:
                Metadata.Produces(404, contentType: "application/problem+json");
                return [new WriteProblemDetailsIfNull(data, identity, message, 404)];

            case OnMissing.EmptyContentWith204:
                Metadata.Produces(204);
                return [new SetStatusCodeAndReturnIfEntityIsNullFrame(data, 204)];

            default:
                return [new ThrowRequiredDataMissingExceptionFrame(data, identity, message)];
        }
    }

    // GET and QUERY (RFC 10008) are the safe, side effect free reads where "there is nothing here" is a
    // benign answer rather than a failure. Anywhere else, a 204 in place of a resource would quietly turn
    // a failed command into an apparent success.
    private static readonly string[] ReadOnlyHttpMethods = ["GET", "QUERY"];

    internal bool IsReadOnlyEndpoint()
    {
        return _httpMethods.Count > 0 && _httpMethods.All(x => ReadOnlyHttpMethods.Contains(x));
    }

    /// <summary>
    ///     The status code written when this endpoint's response body is null. 404 unless
    ///     <see cref="NoContentIfMissingAttribute" /> or <c>WolverineHttpOptions.OnMissingResponseBody</c> says
    ///     otherwise. Resolved once at bootstrapping time by <see cref="ResolveMissingResponseBody" />.
    /// </summary>
    public int MissingResponseBodyStatusCode { get; private set; } = 404;

    private bool _missingResponseBodyIsExplicit;

    /// <summary>
    ///     Read <see cref="NoContentIfMissingAttribute" /> / <see cref="NotFoundIfMissingAttribute" /> off the
    ///     endpoint method, then the endpoint class. Done at construction time rather than in
    ///     <see cref="ResolveMissingResponseBody" /> so that a chain built outside of <see cref="HttpGraph" /> --
    ///     <see cref="ChainFor{T}" /> in tests, most notably -- still honors the attributes.
    /// </summary>
    private void readMissingResponseBodyAttributes()
    {
        if (tryReadMissingResponseBodyAttribute(Method.Method, out var fromMethod))
        {
            MissingResponseBodyStatusCode = fromMethod;
            _missingResponseBodyIsExplicit = true;
        }
        else if (tryReadMissingResponseBodyAttribute(Method.HandlerType, out var fromClass))
        {
            MissingResponseBodyStatusCode = fromClass;
            _missingResponseBodyIsExplicit = true;
        }
    }

    /// <summary>
    ///     Apply the application wide <c>WolverineHttpOptions.OnMissingResponseBody</c> to any endpoint that did
    ///     not declare its own answer.
    /// </summary>
    internal void ResolveMissingResponseBody(WolverineHttpOptions options)
    {
        if (_missingResponseBodyIsExplicit)
        {
            return;
        }

        // The global default deliberately does NOT reach non-read endpoints. Unlike the attribute -- where the
        // author is naming one endpoint and a mistake should be loud -- a single application wide setting would
        // otherwise silently reshape every POST/PUT/DELETE response in the system.
        MissingResponseBodyStatusCode =
            options.OnMissingResponseBody == OnMissingResponseBody.NoContent204 && IsReadOnlyEndpoint() ? 204 : 404;
    }

    private static bool tryReadMissingResponseBodyAttribute(MemberInfo member, out int statusCode)
    {
        if (member.HasAttribute<NoContentIfMissingAttribute>())
        {
            statusCode = 204;
            return true;
        }

        if (member.HasAttribute<NotFoundIfMissingAttribute>())
        {
            statusCode = 404;
            return true;
        }

        statusCode = 404;
        return false;
    }

    /// <summary>
    ///     Fail fast on a [NoContentIfMissing] that could never be honored. Both checks depend only on the
    ///     attributes and the HTTP method, so this runs at construction time -- the same place as the GH-3648
    ///     request body guard -- rather than waiting for the endpoint to be requested and answer wrongly.
    /// </summary>
    private void assertMissingResponseBodyAttributesAreLegal()
    {
        assertNotBothAttributes(Method.Method, "method");
        assertNotBothAttributes(Method.HandlerType, "class");

        if (Method.Method.HasAttribute<NoContentIfMissingAttribute>())
        {
            assertIsReadOnlyEndpointForNoContent(false);
        }
        // Only reached when the method itself said nothing -- a method level [NotFoundIfMissing] is the
        // documented way to keep one non-read endpoint inside a class that is otherwise all GETs.
        else if (Method.HandlerType.HasAttribute<NoContentIfMissingAttribute>() &&
                 !Method.Method.HasAttribute<NotFoundIfMissingAttribute>())
        {
            assertIsReadOnlyEndpointForNoContent(true);
        }
    }

    private void assertNotBothAttributes(MemberInfo member, string level)
    {
        if (member.HasAttribute<NoContentIfMissingAttribute>() && member.HasAttribute<NotFoundIfMissingAttribute>())
        {
            throw new InvalidOperationException(
                $"HTTP endpoint {Method.HandlerType.FullNameInCode()}.{Method.Method.Name} has both " +
                $"[NoContentIfMissing] and [NotFoundIfMissing] on the same {level}. These are mutually " +
                "exclusive -- keep whichever one you meant.");
        }
    }

    private void assertIsReadOnlyEndpointForNoContent(bool fromClass)
    {
        if (IsReadOnlyEndpoint())
        {
            return;
        }

        var placement = fromClass
            ? $"[NoContentIfMissing] is declared on {Method.HandlerType.FullNameInCode()}, which also holds this endpoint. Either move it onto the individual GET/QUERY methods in that class, or mark this one [NotFoundIfMissing]"
            : $"[NoContentIfMissing] is declared on {Method.HandlerType.FullNameInCode()}.{Method.Method.Name}. Remove it";

        throw new InvalidOperationException(
            $"HTTP endpoint {Method.HandlerType.FullNameInCode()}.{Method.Method.Name} is mapped to " +
            $"{(_httpMethods.Count == 0 ? "no HTTP method" : _httpMethods.Join("/"))} {RoutePattern?.RawText}, " +
            $"but {placement}. An empty 204 in place of a response body is only meaningful on the safe, side " +
            "effect free reads -- GET and QUERY. On any other HTTP method it would turn a failed request into " +
            "an apparent success for the caller.");
    }

    /// <summary>
    ///     <see cref="OnMissing.EmptyContentWith204" /> forces the entity to be treated as required on GET or QUERY
    ///     endpoints. Running the endpoint with a null entity so it can return an empty body anyway buys nothing,
    ///     and it is the one configuration where "not required" and "answer 204" contradict each other.
    /// </summary>
    public override bool IsDataRequired(IDataRequirement requirement)
    {
        if (requirement.OnMissing == OnMissing.EmptyContentWith204 && IsReadOnlyEndpoint())
        {
            return true;
        }

        return requirement.Required;
    }

    public override string ToString()
    {
        return _fileName!;
    }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription(this);
        description.AddValue(nameof(HttpMethods), HttpMethods.ToArray());

        description.AddValue("Route", RoutePattern?.RawText ?? string.Empty);

        if (Tags.Any())
        {
            description.AddValue("Tags", Tags.Select(pair => $"{pair.Key} = {pair.Value}").Join(", "));
        }

        description.AddValue("Endpoint", $"{Method.HandlerType.FullNameInCode()}.{Method.MethodSignature}");

        return description;
    }

    public override bool RequiresOutbox()
    {
        return ServiceDependencies(_parent.Container, Type.EmptyTypes).Contains(typeof(IMessageBus)) || ServiceDependencies(_parent.Container, Type.EmptyTypes).Contains(typeof(MessageContext));
    }

    private void applyMetadata()
    {
        if (RoutePattern != null)
        {
            foreach (var parameter in RoutePattern.Parameters)
            {
                Metadata.WithMetadata(new FromRouteMetadata(parameter.Name));
            }
        }

        Metadata
            .WithMetadata(this)
            .WithMetadata(new WolverineMarker())
            .WithMetadata(new HttpMethodMetadata(_httpMethods));
            //.WithMetadata(Method.Method);

        assertMissingResponseBodyAttributesAreLegal();
        readMissingResponseBodyAttributes();

        // Checked outside the HasRequestType branch below on purpose. On a GET a complex parameter binds
        // from the query string rather than the body, so no Accepts metadata is produced -- but the
        // attribute itself still reaches the endpoint metadata through the GetCustomAttributes() loop at
        // the end of this method, and ContentTypeEndpointSelectorPolicy filters candidates on the
        // attribute directly. The endpoint is just as unreachable, via the other policy. See GH-3648.
        if (Method.Method.TryGetAttribute<AcceptsContentTypeAttribute>(out var declaredAccepts))
        {
            assertCanReceiveARequestBody(
                $"it is decorated with [AcceptsContentType(\"{declaredAccepts.ContentTypes.Join("\", \"")}\")]");
        }

        if (HasRequestType && ReadsRequestBody)
        {
            if (IsFormData)
            {
                assertCanReceiveARequestBody("its [AsParameters] or [FromForm] members bind from a form");
                Metadata.Accepts(RequestType, true, "application/x-www-form-urlencoded", "multipart/form-data");
            }
            else if (declaredAccepts != null)
            {
                Metadata.Accepts(RequestType, false, declaredAccepts.ContentTypes[0],
                    declaredAccepts.ContentTypes[1..]);
            }
            else
            {
                Metadata.Accepts(RequestType, false, "application/json");
            }
        }
        else if (FileParameters.Any())
        {
            assertCanReceiveARequestBody(
                $"it takes the file parameter '{FileParameters[0].Name}', which is read from a form");
            Metadata.Accepts(typeof(IFormFile), true, "application/x-www-form-urlencoded", "multipart/form-data");
        }

        applyAntiforgeryMetadata();

        foreach (var attribute in Method.HandlerType.GetCustomAttributes()) Metadata.WithMetadata(attribute);
        foreach (var attribute in Method.Method.GetCustomAttributes()) Metadata.WithMetadata(attribute);
    }

    // GET and HEAD only. DELETE is deliberately NOT included: a request body on DELETE has no defined
    // semantics but is not forbidden, and some APIs do rely on it -- failing those at startup would be a
    // gratuitous break. See GH-3648.
    private static readonly string[] BodylessHttpMethods = ["GET", "HEAD"];

    /// <summary>
    ///     Fail fast when a chain would advertise a request body on an HTTP method that cannot carry one.
    ///     This is never a configuration anyone wants: ASP.NET Core's <c>AcceptsMatcherPolicy</c> compiles
    ///     <c>IAcceptsMetadata</c> into content-type edges in the route matcher, so such an endpoint is
    ///     silently dropped from candidate selection and every request to it returns a bare <b>404</b> --
    ///     not a 415, not an error, and nothing is logged. That is what made GH-3591/GH-3630 so hard to
    ///     diagnose, and the endpoint is unreachable either way. See GH-3648.
    /// </summary>
    private void assertCanReceiveARequestBody(string why)
    {
        if (_httpMethods.Count == 0 || !_httpMethods.All(x => BodylessHttpMethods.Contains(x)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"HTTP endpoint {Method.HandlerType.FullNameInCode()}.{Method.Method.Name} is mapped to " +
            $"{_httpMethods.Join("/")} {RoutePattern?.RawText}, but declares a request body because {why}. " +
            $"A {_httpMethods.Join("/")} request carries no body, so ASP.NET Core would drop this endpoint from " +
            "route matching entirely and every request to it would return 404. Either map it to a method that " +
            "takes a body (POST/PUT/PATCH), or bind from the query string, route, or headers instead.");
    }

    private void applyAntiforgeryMetadata()
    {
        // Check for explicit opt-out via [DisableAntiforgery] on method or class
        if (Method.Method.HasAttribute<DisableAntiforgeryAttribute>() ||
            Method.HandlerType.HasAttribute<DisableAntiforgeryAttribute>())
        {
            Metadata.WithMetadata(WolverineAntiforgeryMetadata.NotRequired);
            return;
        }

        // Check for explicit opt-in via [ValidateAntiforgery] on method or class
        if (Method.Method.HasAttribute<ValidateAntiforgeryAttribute>() ||
            Method.HandlerType.HasAttribute<ValidateAntiforgeryAttribute>())
        {
            Metadata.WithMetadata(WolverineAntiforgeryMetadata.Required);
            return;
        }

        // Auto-enable for form data and file upload endpoints when antiforgery is enabled
        if (_parent.AutoAntiforgeryOnFormEndpoints && (IsFormData || FileParameters.Any()))
        {
            Metadata.WithMetadata(WolverineAntiforgeryMetadata.Required);
        }
    }

    public HttpElementVariable? TryFindOrCreateFormValue(ParameterInfo parameter)
    {
        var parameterName = parameter.Name;
        var key = parameterName;
        var parameterType = parameter.ParameterType;

        if (parameter.TryGetAttribute<FromFormAttribute>(out var att) && att.Name.IsNotEmpty())
        {
            key = att.Name;
        }

        return TryFindOrCreateFormValue(parameterType, parameterName!, key);
    }
    
 public HttpElementVariable? TryFindOrCreateFormValue(Type parameterType, string parameterName, string? key = null){
        key ??= parameterName;
        var variable = _formValueVariables.FirstOrDefault(x => x.Name == key);
        if (variable == null)
        {   
            if (parameterType == typeof(string))
            {
                variable = new ReadHttpFrame(BindingSource.Form, parameterType,key).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }
            if (parameterType == typeof(string[]))
            {
                variable = new ParsedArrayFormValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }

            if (parameterType.IsNullable())
            {
                var inner = parameterType.GetInnerTypeFromNullable();
                if (RouteParameterStrategy.CanParse(inner))
                {
                    variable = new ReadHttpFrame(BindingSource.Form, parameterType,key).Variable;
                    variable.Name = key;
                    _formValueVariables.Add(variable);
                }
            }
            
            if (parameterType.IsArray && RouteParameterStrategy.CanParse(parameterType.GetElementType()!))
            {
                variable = new ParsedArrayFormValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }

            if (ParsedCollectionQueryStringValue.CanParse(parameterType))
            {
                variable = new ParsedCollectionFormValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }

            if (RouteParameterStrategy.CanParse(parameterType))
            {
                variable = new ReadHttpFrame(BindingSource.Form, parameterType,key).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }
        }
        else if (variable.VariableType != parameterType)
        {
            throw new InvalidOperationException(
                $"The form value parameter '{key}' cannot be used for multiple target types");
        }

        return variable;
    }
 
    public bool FindQuerystringVariable(Type variableType, string routeOrParameterName, [NotNullWhen(true)]out Variable? variable)
    {
        var matched = Method.Method.GetParameters()
            .FirstOrDefault(x => x.ParameterType == variableType && x.Name != null && x.Name.EqualsIgnoreCase(routeOrParameterName));
        if (matched is not null)
        {
            variable = TryFindOrCreateQuerystringValue(matched);
            if (variable is not null)
            {
                return true;
            }
        }

        variable = null;
        return false;
    }

    public HttpElementVariable? TryFindOrCreateQuerystringValue(ParameterInfo parameter)
    {
        var parameterName = parameter.Name;
        var key = parameterName;
        var parameterType = parameter.ParameterType;

        if (parameter.TryGetAttribute<FromQueryAttribute>(out var att) && att.Name.IsNotEmpty())
        {
            key = att.Name;
        }

        return TryFindOrCreateQuerystringValue(parameterType, parameterName!, key);
    }

    public HttpElementVariable? TryFindOrCreateQuerystringValue(Type parameterType, string parameterName, string? key = null)
    {
        key ??= parameterName;
        var variable = _querystringVariables.FirstOrDefault(x => x.Name == key);
        if (variable == null)
        {
            variable = createQuerystringValue(parameterType, parameterName, key);
            if (variable != null)
            {
                _querystringVariables.Add(variable);
            }
        }
        else if (variable.VariableType != parameterType)
        {
            throw new InvalidOperationException(
                $"The query string parameter '{key}' cannot be used for multiple target types");
        }

        return variable;
    }

    /// <summary>
    /// Creates the reading frame + variable for a query string value WITHOUT registering it in
    /// <c>_querystringVariables</c>. GH-4314: the postprocessor binding pass in HttpChain.Codegen.cs
    /// needs exactly this creation logic, but that pass runs at codegen time, which is lazy — and
    /// <c>_querystringVariables</c> feeds the OpenAPI description (fillQuerystringParameters), so
    /// registering from codegen would make the rendered description depend on whether the chain
    /// happened to compile before or after the ApiDescription was read.
    /// </summary>
    private HttpElementVariable? createQuerystringValue(Type parameterType, string parameterName, string key)
    {
        HttpElementVariable? variable = null;

        if (parameterType == typeof(string))
        {
            variable = new ReadHttpFrame(BindingSource.QueryString, parameterType, key).Variable;
            variable.Name = key;

            if (variable.Usage == "tenantId")
            {
                variable.OverrideName("tenantIdString");
            }
        }

        if (parameterType == typeof(string[]))
        {
            variable = new ParsedArrayQueryStringValue(parameterType, key).Variable;
            variable.Name = key;
        }

        if (parameterType.IsNullable())
        {
            var inner = parameterType.GetInnerTypeFromNullable();
            if (RouteParameterStrategy.CanParse(inner))
            {
                //variable = new ParsedNullableQueryStringValue(parameterType, parameterName).Variable;
                variable = new ReadHttpFrame(BindingSource.QueryString, parameterType, key,
                    rejectUnparseableValue: _parent.RejectUnparseableQueryValues).Variable;
                variable.Name = key;
            }
        }

        if (parameterType.IsArray && RouteParameterStrategy.CanParse(parameterType.GetElementType()!))
        {
            variable = new ParsedArrayQueryStringValue(parameterType, key,
                rejectUnparseableValue: _parent.RejectUnparseableQueryValues).Variable;
            variable.Name = key;
        }

        if (ParsedCollectionQueryStringValue.CanParse(parameterType))
        {
            variable = new ParsedCollectionQueryStringValue(parameterType, key,
                rejectUnparseableValue: _parent.RejectUnparseableQueryValues).Variable;
            variable.Name = key;
        }

        if (RouteParameterStrategy.CanParse(parameterType))
        {
            //variable = new ParsedQueryStringValue(parameterType, parameterName).Variable;
            variable = new ReadHttpFrame(BindingSource.QueryString, parameterType, parameterName,
                rejectUnparseableValue: _parent.RejectUnparseableQueryValues).Variable;
            variable.Name = key;
        }

        return variable;
    }

    private readonly Dictionary<string, Type> _declaredRouteParameterTypes = new(StringComparer.OrdinalIgnoreCase);

    IReadOnlyList<string> IRoutedChain.RouteParameterNames =>
        RoutePattern?.Parameters.Select(x => x.Name).ToArray() ?? [];

    void IRoutedChain.DeclareRouteParameterType(string routeParameterName, Type parameterType)
    {
        if (RoutePattern == null) return;
        if (!RoutePattern.Parameters.Any(x => x.Name.EqualsIgnoreCase(routeParameterName))) return;

        _declaredRouteParameterTypes[routeParameterName] = parameterType;
    }

    /// <summary>
    /// The CLR type declared for a route parameter by middleware that binds it outside of the endpoint
    /// method signature — see <see cref="IRoutedChain.DeclareRouteParameterType"/>. Only consulted when
    /// nothing else in the chain, and no route constraint, can type the parameter. See GH-3420.
    /// </summary>
    private Type? declaredRouteParameterType(string routeParameterName)
    {
        return _declaredRouteParameterTypes.TryGetValue(routeParameterName, out var type) ? type : null;
    }

    public bool FindRouteVariable(ParameterInfo parameter, [NotNullWhen(true)]out Variable? variable)
    {
        var existing = _routeVariables.FirstOrDefault(x =>
            x.VariableType == parameter.ParameterType && x.Usage.EqualsIgnoreCase(parameter.Name!));

        if (existing is not null)
        {
            variable = existing;
            return true;
        }

        var matchingRouteParameter = RoutePattern!.Parameters.FirstOrDefault(x => x.Name == parameter.Name);
        if (matchingRouteParameter != null)
        {
            var isOptional = matchingRouteParameter.IsOptional;
            
            if (parameter.ParameterType == typeof(string))
            {
                variable = new ReadHttpFrame(BindingSource.RouteValue, typeof(string), parameter.Name!, isOptional).Variable;
                _routeVariables.Add(variable);
                return true;
            }
            
            if (parameter.ParameterType.IsNullable())
            {
                var inner = parameter.ParameterType.GetInnerTypeFromNullable();
                if (RouteParameterStrategy.CanParse(inner))
                {
                    variable = new ReadHttpFrame(BindingSource.RouteValue, parameter.ParameterType, parameter.Name!, isOptional).Variable;
                    _routeVariables.Add(variable);
                    return true;
                }
            }

            if (RouteParameterStrategy.CanParse(parameter.ParameterType))
            {
                variable = new ReadHttpFrame(BindingSource.RouteValue, parameter.ParameterType, parameter.Name!, isOptional).Variable;
                _routeVariables.Add(variable);
                return true;
            }
        }

        variable = default;
        return false;
    }

    public bool FindRouteVariable(Type variableType, string routeOrParameterName, [NotNullWhen(true)]out Variable? variable)
    {
        var matched = _routeVariables.OfType<HttpElementVariable>()
            .FirstOrDefault(x => x.VariableType == variableType && x.Name.EqualsIgnoreCase(routeOrParameterName));
        if (matched is not null)
        {
            variable = matched;
            return true;
        }

        var matches = RoutePattern!.Parameters.Any(x => x.Name.EqualsIgnoreCase(routeOrParameterName));
        if (matches)
        {
            if (variableType == typeof(string) || RouteParameterStrategy.CanParse(variableType))
            {
                var frame = new ReadHttpFrame(BindingSource.RouteValue, variableType, routeOrParameterName)
                {
                    Key = routeOrParameterName
                };
                
                variable = frame.Variable;
                _routeVariables.Add(variable);
                return true;
            }
        }

        variable = default;
        return false;

    }

    private readonly List<HttpElementVariable> _headerVariables = [];

    public HttpElementVariable GetOrCreateHeaderVariable(IFromHeaderMetadata metadata, ParameterInfo parameter)
    {
        var existing =
            _headerVariables.FirstOrDefault(x => x.Name == metadata.Name && x.VariableType == parameter.ParameterType);

        if (existing != null) return existing;

        var frame = new ReadHttpFrame(BindingSource.Header, parameter.ParameterType, parameter.Name!)
        {
            Key = metadata.Name ?? parameter.Name!
        };
        
        _headerVariables.Add(frame.Variable);
        
        return frame.Variable;
    }
    
    public HttpElementVariable GetOrCreateHeaderVariable(IFromHeaderMetadata metadata, PropertyInfo property)
    {
        var existing =
            _headerVariables.FirstOrDefault(x => x.Name == metadata.Name && x.VariableType == property.PropertyType);

        if (existing != null) return existing;

        var frame = new ReadHttpFrame(BindingSource.Header, property.PropertyType, property.Name)
        {
            Key = metadata.Name ?? property.Name
        };
        
        _headerVariables.Add(frame.Variable);
        
        return frame.Variable;
    }

    string IEndpointNameMetadata.EndpointName => HasExplicitOperationId ? OperationId : ToString();

    string IEndpointSummaryMetadata.Summary => EndpointSummary ?? ToString();

    /// <summary>
    /// Sets an explicit operation ID (endpoint name) and marks it as explicit so it is used
    /// as the endpoint name in the ASP.NET Core routing infrastructure. This is used by policies
    /// that need to disambiguate endpoints that share the same handler method name (e.g. 
    /// <see cref="ApiVersioning.ApiVersioningPolicy"/>).
    /// </summary>
    internal void SetExplicitOperationId(string operationId)
    {
        OperationId = operationId;
        HasExplicitOperationId = true;
    }

    public List<ParameterInfo> FileParameters { get; } = [];

    [MemberNotNullWhen(true, nameof(RequestType))]
    public bool HasRequestType => RequestType != null && RequestType != typeof(void);

    public bool IsFormData { get; internal set; }

    /// <summary>
    ///     True when this chain actually reads a request body — a JSON body, a form, or uploaded files.
    ///     An <c>[AsParameters]</c> type whose members all bind from the query string, route, or headers
    ///     reads no body at all, so the chain must not advertise <c>Accepts</c> metadata for one: ASP.NET
    ///     Core's <c>AcceptsMatcherPolicy</c> builds content-type edges into the route matcher from that
    ///     metadata, and a request that carries no matching Content-Type is then dropped from candidate
    ///     selection entirely — a 404, not a 415. See GH-3630.
    /// </summary>
    internal bool ReadsRequestBody { get; set; } = true;

    public Type? ComplexQueryStringType { get; set; }

    /// <summary>
    /// When using [AsParameters], this tracks the original AsParameters type even when
    /// RequestType is overwritten by a [FromBody] property. This allows middleware like
    /// FluentValidation to also validate the AsParameters type itself.
    /// </summary>
    public Type? AsParametersType { get; internal set; }

    /// <summary>
    /// The codegen variable for the object bound from an <c>[AsParameters]</c> parameter, if any.
    /// Other middleware that searches for a value with <see cref="ValueSource.Anything"/> (notably the
    /// Marten <c>[ReadAggregate]</c>/<c>[WriteAggregate]</c> aggregate-id resolution) reads members off
    /// this object rather than re-using the route/query read frames that <c>AsParametersBindingFrame</c>
    /// owns and generates inline — sharing those owned frames produces a cyclic Next reference and a
    /// StackOverflow during code generation.
    /// </summary>
    public Variable? AsParametersVariable { get; internal set; }

    public ServiceProviderSource ServiceProviderSource { get; set; } = ServiceProviderSource.IsolatedAndScoped;

    internal Variable BuildJsonDeserializationVariable()
    {
        return _parent.BuildJsonDeserializationVariable(this);
    }

    public override void ApplyParameterMatching(MethodCall call)
    {
        _parent.ApplyParameterMatching(this, call);
    }

    public override IdempotencyStyle Idempotency
    {
        get => IdempotencyStyle.None;
        set
        {
            // Nothing, you can't actually override it
        }
    }

    public bool TryReplaceServiceProvider(out Variable serviceProvider)
    {
        serviceProvider = default!;
        if (ServiceProviderSource == ServiceProviderSource.IsolatedAndScoped) return false;

        serviceProvider = new Variable(typeof(IServiceProvider), $"httpContext.{nameof(HttpContext.RequestServices)}");
        return true;
    }
}

