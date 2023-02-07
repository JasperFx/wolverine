using System.Text.Json;
using JasperFx.CodeGeneration;
using Lamar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Wolverine.Http.Resources;

namespace Wolverine.Http;

public partial class EndpointGraph : EndpointDataSource, ICodeFileCollection, IChangeToken
{
    public static readonly string Context = "httpContext";
    private readonly WolverineOptions _options;

    private readonly List<IResourceWriterPolicy> _writerPolicies = new()
    {
        new StringResourceWriterPolicy(),
        new JsonResourceWriterPolicy()
    };

    private readonly List<EndpointChain> _chains = new();
    private readonly List<RouteEndpoint> _endpoints = new();


    public EndpointGraph(WolverineOptions options, IContainer container)
    {
        _options = options;
        Container = container;
        Rules = _options.Node.CodeGeneration;
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

    public void DiscoverEndpoints()
    {
        var source = new EndpointSource(_options.Assemblies);
        var calls = source.FindActions();

        _chains.AddRange(calls.Select(x => new EndpointChain(x, this)));
        
        _endpoints.AddRange(_chains.Select(x => x.BuildEndpoint()));
    }

    public override IChangeToken GetChangeToken()
    {
        return this;
    }

    public EndpointChain? ChainFor(string httpMethod, string urlPattern)
    {
        return _chains.FirstOrDefault(x => x.HttpMethods.Contains(httpMethod) && x.RoutePattern.RawText == urlPattern);
    }
}

