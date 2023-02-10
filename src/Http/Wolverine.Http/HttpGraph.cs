using System.Text.Json;
using JasperFx.CodeGeneration;
using Lamar;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Wolverine.Configuration;
using Wolverine.Http.Resources;
using Endpoint = Microsoft.AspNetCore.Http.Endpoint;

namespace Wolverine.Http;

public partial class HttpGraph : EndpointDataSource, ICodeFileCollection, IChangeToken
{
    public static readonly string Context = "httpContext";
    private readonly WolverineOptions _options;

    private readonly List<IResourceWriterPolicy> _writerPolicies = new()
    {
        new ResultWriterPolicy(),
        new StringResourceWriterPolicy(),
        new JsonResourceWriterPolicy()
    };

    private readonly List<HttpChain> _chains = new();
    private readonly List<RouteEndpoint> _endpoints = new();


    public HttpGraph(WolverineOptions options, IContainer container)
    {
        _options = options;
        Container = container;
        Rules = _options.CodeGeneration;
    }

    internal IContainer Container { get; }

    internal IEnumerable<IResourceWriterPolicy> WriterPolicies => _writerPolicies;

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

    public void DiscoverEndpoints(WolverineHttpOptions wolverineHttpOptions)
    {
        var source = new HttpChainSource(_options.Assemblies);
        var calls = source.FindActions();

        _chains.AddRange(calls.Select(x => new HttpChain(x, this)));

                
        wolverineHttpOptions.Middleware.Apply(_chains, Rules, Container);

        var policies = _options.Policies.OfType<IChainPolicy>();
        foreach (var policy in policies)
        {
            policy.Apply(_chains, Rules, Container);
        }

        foreach (var policy in wolverineHttpOptions.Policies)
        {
            policy.Apply(_chains, Rules, Container);
        }

        _endpoints.AddRange(_chains.Select(x => x.BuildEndpoint()));
    }

    public override IChangeToken GetChangeToken()
    {
        return this;
    }

    public HttpChain? ChainFor(string httpMethod, string urlPattern)
    {
        return _chains.FirstOrDefault(x => x.HttpMethods.Contains(httpMethod) && x.RoutePattern.RawText == urlPattern);
    }
}

