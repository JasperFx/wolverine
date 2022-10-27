using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using Oakton.Resources;
using Spectre.Console;
using Spectre.Console.Rendering;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public interface IBrokerTransport : ITransport
{
    /// <summary>
    /// Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    bool AutoProvision { get; set; }

    /// <summary>
    /// Should Wolverine attempt to purge all messages out of existing or discovered queues
    /// on application start up? This can be useful for testing, and occasionally for ephemeral
    /// messages
    /// </summary>
    bool AutoPurgeAllQueues { get; set; }

    ValueTask ConnectAsync(IWolverineRuntime logger);

    /// <summary>
    /// This helps to create a diagnostic table of broker state
    /// </summary>
    /// <returns></returns>
    IEnumerable<PropertyColumn> DiagnosticColumns();
}

public interface IBrokerEndpoint
{
    ValueTask<bool> CheckAsync();
    ValueTask TeardownAsync(ILogger logger);
    ValueTask SetupAsync(ILogger logger);
    
    Uri Uri { get; }
}

public interface IBrokerQueue : IBrokerEndpoint
{
    ValueTask PurgeAsync(ILogger logger);
    ValueTask<Dictionary<string, string>> GetAttributesAsync();
}

public record PropertyColumn(string Header, string AttributeName, Justify Alignment = Justify.Left)
{
    public IRenderable BuildCell(Dictionary<string, string> dict)
    {
        if (dict.TryGetValue(AttributeName, out var value))
        {
            if (value != null)
            {
                return new Markup(value);
            }
        }
        
        return new Markup("-");
    }
}

public class BrokerResource : IStatefulResource
{
    private readonly IBrokerTransport _transport;
    private readonly IWolverineRuntime _runtime;

    public BrokerResource(IBrokerTransport transport, IWolverineRuntime runtime)
    {
        _transport = transport;
        _runtime = runtime;
    }

    public async Task Check(CancellationToken token)
    {
        var missing = new List<Uri>();
        await _transport.ConnectAsync(_runtime);

        foreach (var endpoint in _transport.Endpoints().OfType<IBrokerEndpoint>())
        {
            try
            {
                var exists = await endpoint.CheckAsync();
                if (!exists)
                {
                    missing.Add(endpoint.Uri);
                }
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Error while checking the existence of required broker endpoint {Uri}", endpoint.Uri);
                missing.Add(endpoint.Uri);
            }
        }
        
        if (missing.Any())
        {
            throw new Exception($"Missing known broker resources: {missing.Select(x => x.ToString()).Join(", ")}");
        }
    }

    public async Task ClearState(CancellationToken token)
    {
        await _transport.ConnectAsync(_runtime);
        foreach (var queue in _transport.Endpoints().OfType<IBrokerQueue>())
        {
            await queue.PurgeAsync(_runtime.Logger);
        }
    }

    public async Task Teardown(CancellationToken token)
    {
        await _transport.ConnectAsync(_runtime);

        foreach (var endpoint in _transport.Endpoints().OfType<IBrokerEndpoint>())
        {
            try
            {
                await endpoint.TeardownAsync(_runtime.Logger);
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Error while attempting to tear down broker endpoint {Uri}", endpoint.Uri);
            }
        }
    }

    public async Task Setup(CancellationToken token)
    {
        await _transport.ConnectAsync(_runtime);

        foreach (var endpoint in _transport.Endpoints().OfType<IBrokerEndpoint>())
        {
            try
            {
                await endpoint.SetupAsync(_runtime.Logger);
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Error while attempting to tear down broker endpoint {Uri}", endpoint.Uri);
            }
        }
    }

    public async Task<IRenderable> DetermineStatus(CancellationToken token)
    {
        var table = new Table
        {
            Alignment = Justify.Left
        };

        var columns = _transport.DiagnosticColumns().ToArray();
        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn(column.Header) { Alignment = column.Alignment });
        }
        
        await _transport.ConnectAsync(_runtime);

        foreach (var endpoint in _transport.Endpoints().OfType<IBrokerQueue>())
        {
            try
            {
                var dict = await endpoint.GetAttributesAsync();
                var cells = columns.Select(x => x.BuildCell(dict));
                table.AddRow(cells);
            }
            catch (Exception e)
            {
                _runtime.Logger.LogError(e, "Error while attempting to determine statist broker endpoint {Uri}", endpoint.Uri);
            }
        }

        return table;
    }

    public string Type => TransportConstants.WolverineTransport;
    public string Name => _transport.Name;
}

/// <summary>
/// Abstract base class suitable for brokered messaging infrastructure
/// </summary>
/// <typeparam name="TEndpoint"></typeparam>
public abstract class BrokerTransport<TEndpoint> : TransportBase<TEndpoint>, IBrokerTransport where TEndpoint : Endpoint, IBrokerEndpoint
{
    protected BrokerTransport(string protocol, string name) : base(protocol, name)
    {
    }
    
    /// <summary>
    /// Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    public bool AutoProvision { get; set; }

    /// <summary>
    /// Should Wolverine attempt to purge all messages out of existing or discovered queues
    /// on application start up? This can be useful for testing, and occasionally for ephemeral
    /// messages
    /// </summary>
    public bool AutoPurgeAllQueues { get; set; }


    //public abstract ValueTask ConnectAsync();
    protected virtual void tryBuildResponseQueueEndpoint(IWolverineRuntime runtime)
    {
        
    }
    
    public sealed override bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = new BrokerResource(this, runtime);
        return true;
    }

    public abstract ValueTask ConnectAsync(IWolverineRuntime logger);
    public abstract IEnumerable<PropertyColumn> DiagnosticColumns();

    public sealed override async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        tryBuildResponseQueueEndpoint(runtime);

        await ConnectAsync(runtime);

        foreach (var endpoint in endpoints())
        {
            await endpoint.InitializeAsync(runtime.Logger);
        }
    }
}