using Oakton.Descriptions;
using Spectre.Console;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IDescribedSystemPartFactory
{
    IDescribedSystemPart[] IDescribedSystemPartFactory.Parts()
    {
        Handlers.Compile(Options, _container);

        return new IDescribedSystemPart[] { Options, Handlers, new ListenersDescription(this), new SenderDescription(this)};
    }
}

internal class SenderDescription : IDescribedSystemPart, IWriteToConsole
{
    private readonly WolverineRuntime _runtime;

    public SenderDescription(WolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task Write(TextWriter writer)
    {
        await writer.WriteLineAsync("Use the console output option.");
    }

    public string Title => "Wolverine Subscribers";
    public async Task WriteToConsole()
    {
        // "start" the Wolverine app in a lightweight way
        // to discover endpoints, but don't start the actual
        // external endpoint listening or sending
        await _runtime.StartLightweightAsync();

        var table = new Table();

        table.AddColumn("Uri (Name)");
        table.AddColumn("Mode");
        table.AddColumn("Serializers");
        table.AddColumn("Subscriptions");

        var senders = _runtime
            .Options
            .Transports
            .SelectMany(x => x.Endpoints())
            .Where(x => x.Subscriptions.Any())
            .OrderBy(x => x.Uri);
        
        foreach (var endpoint in senders)
        {
            table.AddRow(
                endpoint.NameDescription(),
                new Markup(endpoint.Mode.ToString()),
                endpoint.SerializerDescription(_runtime.Options),
                endpoint.SubscriptionsDescription()
            );
        }
        
        AnsiConsole.Write(table);
    }
}

internal class ListenersDescription : IDescribedSystemPart, IWriteToConsole
{
    private readonly WolverineRuntime _runtime;

    public ListenersDescription(WolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task Write(TextWriter writer)
    {
        await writer.WriteLineAsync("Use the console output option.");
    }

    public string Title => "Wolverine Listeners";
    public async Task WriteToConsole()
    {
        // "start" the Wolverine app in a lightweight way
        // to discover endpoints, but don't start the actual
        // external endpoint listening or sending
        await _runtime.StartLightweightAsync();
        
        var table = new Table();

        // TODO -- add buffering
        // TODO -- circuit breaker
        
        table.AddColumn("Uri (Name)");
        table.AddColumn("Role");
        table.AddColumn("Mode");
        table.AddColumn("Execution");
        table.AddColumn("Serializers");

        var listeners = _runtime
            .Options
            .Transports
            .SelectMany(x => x.Endpoints())
            .Where(x => x.IsListener || x is LocalQueue)
            .OrderBy(x => x.EndpointName);
        
        foreach (var listener in listeners)
        {
            table.AddRow(
                listener.NameDescription(),
                new Markup(listener.Role.ToString()),
                new Markup(listener.Mode.ToString()),
                listener.ExecutionDescription(),
                listener.SerializerDescription(_runtime.Options)
            );
        }

        AnsiConsole.Write(table);
    }
}
