using System.Data;
using JasperFx.CodeGeneration;
using Oakton.Descriptions;
using Spectre.Console;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IDescribedSystemPartFactory
{
    IDescribedSystemPart[] IDescribedSystemPartFactory.Parts()
    {
        Handlers.Compile(Options, _container);

        return new IDescribedSystemPart[] { Options, Handlers, new ListenersDescription(this), new MessageSubscriptions(this), new SenderDescription(this)};
    }
}

internal class MessageSubscriptions : IDescribedSystemPart, IWriteToConsole
{
    private readonly WolverineRuntime _runtime;

    public MessageSubscriptions(WolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task Write(TextWriter writer)
    {
        await writer.WriteLineAsync("Use the console output option.");
    }

    public string Title => "Wolverine Message Routing";
    public async Task WriteToConsole()
    {
        // "start" the Wolverine app in a lightweight way
        // to discover endpoints, but don't start the actual
        // external endpoint listening or sending
        await _runtime.StartLightweightAsync();

        var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);

        if (!messageTypes.Any())
        {
            AnsiConsole.Markup("[gray]No message routes[/gray]");
            return;
        }
        
        writeMessageRouting(messageTypes);
    }

    private void writeMessageRouting(IReadOnlyList<Type> messageTypes)
    {
        var table = new Table().AddColumns("Message Type", "Destination", "Content Type");
        foreach (var messageType in messageTypes.OrderBy(x => x.FullName))
        {
            var routes = _runtime.RoutingFor(messageType).Routes;
            foreach (var route in routes)
            {
                table.AddRow(messageType.FullNameInCode(), route.Sender.Destination.ToString(),
                    route.Serializer.ContentType);
            }
        }

        AnsiConsole.Write(table);
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
        
        // This just forces Wolverine to go find and build any extra sender agents
        var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);
        foreach (var messageType in messageTypes)
        {
            _runtime.RoutingFor(messageType);
        }
            

        writeEndpoints();
    }

    private void writeEndpoints()
    {
        var table = new Table();

        table.AddColumn("Uri (Name)", c => c.NoWrap = true);
        table.AddColumn("Mode");
        table.AddColumn("Serializers", c => c.NoWrap = true);

        var senders = _runtime
            .Options
            .Transports
            .SelectMany(x => x.Endpoints())
            .OrderBy(x => x.Uri.ToString());

        foreach (var endpoint in senders)
        {
            table.AddRow(
                endpoint.NameDescription(),
                new Markup(endpoint.Mode.ToString()),
                endpoint.SerializerDescription(_runtime.Options)
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
                new Markup(listener.Mode.ToString()),
                listener.ExecutionDescription(),
                listener.SerializerDescription(_runtime.Options)
            );
        }

        AnsiConsole.Write(table);
    }
}
