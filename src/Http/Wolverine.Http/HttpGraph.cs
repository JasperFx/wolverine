using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Resources;
using Wolverine.Runtime;
using Endpoint = Microsoft.AspNetCore.Http.Endpoint;

namespace Wolverine.Http;

public partial class HttpGraph : EndpointDataSource, ICodeFileCollectionWithServices, IChangeToken, IDescribedSystemPart,
    IWriteToConsole
{
    public static readonly string Context = "httpContext";

    private readonly List<HttpChain> _chains = [];
    private readonly List<RouteEndpoint> _endpoints = [];
    private readonly WolverineOptions _options;

    private readonly List<IResourceWriterPolicy> _builtInWriterPolicies =
    [
        new EmptyBody204Policy(),
        new StatusCodePolicy(),
        new ResultWriterPolicy(),
        new StringResourceWriterPolicy(),
        new JsonResourceWriterPolicy()
    ];

    private readonly List<IResourceWriterPolicy> _optionsWriterPolicies = [];

    public HttpGraph(WolverineOptions options, IServiceContainer container)
    {
        _options = options;
        Container = container;
        Rules = _options.CodeGeneration;
    }

    public IReadOnlyList<HttpChain> Chains => _chains;

    internal IServiceContainer Container { get; }

    internal IEnumerable<IResourceWriterPolicy> WriterPolicies => _optionsWriterPolicies.Concat(_builtInWriterPolicies);

    public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

    IDisposable IChangeToken.RegisterChangeCallback(Action<object?> callback, object? state)
    {
        return new StubDisposable();
    }

    bool IChangeToken.ActiveChangeCallbacks => false;

    bool IChangeToken.HasChanged => false;

    public IReadOnlyList<ICodeFile> BuildFiles()
    {
        return _chains;
    }

    public string ChildNamespace => "WolverineHandlers";
    public GenerationRules Rules { get; }

    Task IDescribedSystemPart.Write(TextWriter writer)
    {
        return writer.WriteLineAsync("Use console output.");
    }

    string IDescribedSystemPart.Title => "Wolverine Http Endpoints";

    Task IWriteToConsole.WriteToConsole()
    {
        var table = new Table()
            .AddColumns("Route", "Http Method", "Handler Method", "Generated Type Name");

        foreach (var chain in _chains.OrderBy(x => x.RoutePattern!.RawText))
        {
            var handlerCode = $"{chain.Method.HandlerType.FullNameInCode()}.{chain.Method.Method.Name}()";
            var verbs = chain.HttpMethods.Select(x => x.ToUpper()).Join("/");

            table.AddRow(chain.RoutePattern!.RawText.EscapeMarkup(), verbs, handlerCode.EscapeMarkup(),
                chain.Description.EscapeMarkup());
        }

        AnsiConsole.Write(table);

        return Task.CompletedTask;
    }

    public void DiscoverEndpoints(WolverineHttpOptions wolverineHttpOptions)
    {
        var source = new HttpChainSource(_options.Assemblies);
        var logger = Container.GetInstance<ILogger<HttpGraph>>();

        var calls = source.FindActions();
        logger.LogInformation("Found {Count} Wolverine HTTP endpoints in assemblys {Assemblies}", calls.Length, _options.Assemblies.Select(x => x.GetName().Name).Join(", "));
        if (calls.Length == 0)
        {
            logger.LogWarning("Found no Wolverine HTTP endpoints. If this is not expected, check the assemblies being scanned. See https://wolverine.netlify.app/guide/http/integration.html#discovery for more information");
        }

        _chains.AddRange(calls.Select(x => new HttpChain(x, this)));

        wolverineHttpOptions.Middleware.Apply(_chains, Rules, Container);
        _optionsWriterPolicies.AddRange(wolverineHttpOptions.ResourceWriterPolicies);

        var policies = _options.Policies.OfType<IChainPolicy>();
        foreach (var policy in policies)
        {
            policy.Apply(_chains, Rules, Container);
        }

        foreach (var policy in wolverineHttpOptions.Policies) policy.Apply(_chains, Rules, Container);

        _endpoints.AddRange(_chains.Select(x => x.BuildEndpoint()));
    }

    public override IChangeToken GetChangeToken()
    {
        return this;
    }

    public HttpChain? ChainFor(string httpMethod, [StringSyntax("Route")]string urlPattern)
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

    internal void UseNewtonsoftJson()
    {
        _builtInWriterPolicies.OfType<JsonResourceWriterPolicy>().Single().Usage = JsonUsage.NewtonsoftJson;
        _strategies.OfType<JsonBodyParameterStrategy>().Single().Usage = JsonUsage.NewtonsoftJson;
    }
}