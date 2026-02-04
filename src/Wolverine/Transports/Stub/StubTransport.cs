using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Stubs;

namespace Wolverine.Transports.Stub;

internal class StubTransport : TransportBase<StubEndpoint>, IStubHandlers, IMessageRouteSource, IExecutorFactory
{
    private WolverineRuntime _runtime;
    
    private readonly Dictionary<Type, IMessageHandler> _stubs = new();

    public bool HasAny() => _stubs.Any();

    public StubTransport() : base("stub", "Stub", [])
    {
        Endpoints =
            new LightweightCache<string, StubEndpoint>(name => new StubEndpoint(name, this));

        Endpoints[TransportConstants.Replies].IsListener = true;

        var endpoint = Endpoints["system"];
        endpoint.Role = EndpointRole.System;
        endpoint.Mode = EndpointMode.Inline;
    }

    public override bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    public new LightweightCache<string, StubEndpoint> Endpoints { get; }

    protected override IEnumerable<StubEndpoint> endpoints()
    {
        return Endpoints;
    }

    protected override StubEndpoint findEndpointByUri(Uri uri)
    {
        var name = uri.Host;
        return Endpoints[name];
    }

    public override ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        _runtime = (WolverineRuntime)runtime;
        
        foreach (var endpoint in Endpoints)
        {
            endpoint.Compile(runtime);

            if (endpoint.Uri == TransportConstants.LocalStubs)
            {
                endpoint.Start(new HandlerPipeline((WolverineRuntime)runtime, runtime.Options.Transports.GetOrCreate<StubTransport>(), endpoint), runtime.MessageTracking);
            }
            else
            {
                endpoint.Start(new HandlerPipeline((WolverineRuntime)runtime, (IExecutorFactory)runtime, endpoint), runtime.MessageTracking);
            }
            
        }

        return ValueTask.CompletedTask;
    }
    
    IEnumerable<IMessageRoute> IMessageRouteSource.FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (_stubs.ContainsKey(messageType))
        {
            var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.LocalStubs, e => e.Mode = EndpointMode.BufferedInMemory);
            
            yield return new MessageRoute(messageType, sendingAgent.Endpoint, runtime);
        }
    }

    public override Endpoint? ReplyEndpoint()
    {
        return Endpoints[TransportConstants.Replies];
    }

    public void Stub<T>(Func<T, IMessageContext, IServiceProvider, CancellationToken, Task> func)
    {
        if (_stubs.ContainsKey(typeof(T)))
        {
            _stubs[typeof(T)].As<StubMessageHandler<T>>().Func = func;
        }
        else
        {
            _stubs[typeof(T)] = new StubMessageHandler<T>(func, _runtime.Services);
        }

        _runtime.ClearRoutingFor(typeof(T));

    }
    
    public void Stub<TRequest, TResponse>(Func<TRequest, TResponse> func)
    {
        Stub<TRequest>(async (message, context, _, _) =>
        {
            var response = func(message);
            await context.As<MessageContext>().EnqueueCascadingAsync(response);
        });
    }

    public void Clear<T>()
    {
        _runtime.ClearRoutingFor(typeof(T));
        _stubs.Remove(typeof(T));
    }

    public void ClearAll()
    {
        foreach (var messageType in _stubs.Keys)
        {
            _runtime.ClearRoutingFor(messageType);
            _stubs.Remove(messageType);
        }
    }

    bool IMessageRouteSource.IsAdditive => false;
    

    IExecutor IExecutorFactory.BuildFor(Type messageType)
    {

        if (_stubs.TryGetValue(messageType, out var handler))
        {
            return new Executor(_runtime.ExecutionPool, _runtime.Logger, handler, _runtime.MessageTracking,
                new FailureRuleCollection(), 10.Seconds());
        }

        throw new ArgumentOutOfRangeException(nameof(messageType),
            "No registered stub for message type " + messageType.FullNameInCode());


    }

    IExecutor IExecutorFactory.BuildFor(Type messageType, Endpoint endpoint)
    {
        if (_stubs.TryGetValue(messageType, out var handler))
        {
            return new Executor(_runtime.ExecutionPool, _runtime.Logger, handler, _runtime.MessageTracking,
                new FailureRuleCollection(), 10.Seconds());
        }

        throw new ArgumentOutOfRangeException(nameof(messageType),
            "No registered stub for message type " + messageType.FullNameInCode());
    }
}