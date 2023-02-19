using Oakton.Descriptions;
using Spectre.Console;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IDescribedSystemPartFactory
{
    IDescribedSystemPart[] IDescribedSystemPartFactory.Parts()
    {
        Handlers.Compile(Options, _container);

        return new IDescribedSystemPart[] { Options, Handlers, new ListenersDescription(this)};
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
        await WriteToConsole();
    }

    public string Title => "Wolverine Listeners";
    public async Task WriteToConsole()
    {
        // "start" the Wolverine app in a lightweight way
        // to discover endpoints, but don't start the actual
        // external endpoint listening or sending
        await _runtime.StartLightweightAsync();
        
        var table = new Table();

        table.AddColumn("Uri (Name)");
        table.AddColumn("Role");
        table.AddColumn("Mode");
        table.AddColumn("Execution");
        table.AddColumn("Serializers");

        var listeners = _runtime
            .Options
            .Transports
            .SelectMany(x => x.Endpoints())
            .Where(x => x.IsListener)
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
