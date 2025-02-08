using JasperFx.Core;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Spectre.Console;
using Spectre.Console.Rendering;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public class BrokerResource : IStatefulResource
{
    private readonly IWolverineRuntime _runtime;
    private readonly IBrokerTransport _transport;

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
                _runtime.Logger.LogError(e, "Error while checking the existence of required broker endpoint {Uri}",
                    endpoint.Uri);
                missing.Add(endpoint.Uri);
            }
        }

        if (missing.Count != 0)
        {
            throw new Exception($"Missing known broker resources: {missing.Select(x => x.ToString()).Join(", ")}");
        }
    }

    public async Task ClearState(CancellationToken token)
    {
        await _transport.ConnectAsync(_runtime);
        foreach (var queue in _transport.Endpoints().OfType<IBrokerQueue>()) await queue.PurgeAsync(_runtime.Logger);
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
                _runtime.Logger.LogError(e, "Error while attempting to setup broker endpoint {Uri}", endpoint.Uri);
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
        if (columns.Length == 0) return table;

        foreach (var column in columns)
            table.AddColumn(new TableColumn(column.Header) { Alignment = column.Alignment });

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
                _runtime.Logger.LogError(e, "Error while attempting to determine statist broker endpoint {Uri}",
                    endpoint.Uri);
            }
        }

        return table;
    }

    public string Type => TransportConstants.WolverineTransport;
    public string Name => _transport.Name;
}