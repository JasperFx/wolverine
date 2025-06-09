using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;

namespace Wolverine;

internal class WolverineSystemPart : SystemPartBase
{
    private readonly WolverineRuntime _runtime;

    public WolverineSystemPart(IWolverineRuntime runtime) : base("Wolverine", new Uri("wolverine://" + runtime.Options.ServiceName))
    {
        _runtime = (WolverineRuntime)runtime;
    }

    public override async Task WriteToConsole()
    {
        await _runtime.StartLightweightAsync();
        
        _runtime.Options.WriteToConsole();
        
        await _runtime.Options.HandlerGraph.WriteToConsole();
        WriteMessageSubscriptions();
        WriteSendingEndpoints();
        WriteListeners();
        WriteErrorHandling();
    }
    
    public void WriteMessageSubscriptions()
    {
        AnsiConsole.Write("Message Routing");
        var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);

        if (!messageTypes.Any())
        {
            AnsiConsole.Markup("[gray]No message routes[/]");
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
            foreach (var route in routes.OfType<MessageRoute>())
            {
                table.AddRow(messageType.FullNameInCode(), route.Sender.Destination.ToString(),
                    route.Serializer.ContentType);
            }
        }

        AnsiConsole.Write(table);
    }
    
    public void WriteSendingEndpoints()
    {
        AnsiConsole.Write("Sending Endpoints");

        // This just forces Wolverine to go find and build any extra sender agents
        var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);
        foreach (var messageType in messageTypes) _runtime.RoutingFor(messageType);


        writeEndpoints();
    }

    private void writeEndpoints()
    {
        var table = new Table();

        table.AddColumn("Uri", c => c.NoWrap = true);
        table.AddColumn("Name");
        table.AddColumn("Mode");
        table.AddColumn("Serializer(s)", c => c.NoWrap = true);

        var senders = _runtime
            .Options
            .Transports
            .SelectMany(x => x.Endpoints())
            .OrderBy(x => x.Uri.ToString());

        foreach (var endpoint in senders)
        {
            table.AddRow(
                endpoint.Uri.ToString(),
                endpoint.EndpointName,
                endpoint.Mode.ToString(),
                endpoint.SerializerDescription(_runtime.Options)
            );
        }

        AnsiConsole.Write(table);
    }
    
    public void WriteListeners()
    {
        AnsiConsole.Write("Listeners");
        
        var table = new Table();

        table.AddColumn("Uri");
        table.AddColumn("Name");
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
                listener.Uri.ToString(),
                listener.EndpointName,
                listener.Mode.ToString(),
                listener.ExecutionDescription(),
                listener.SerializerDescription(_runtime.Options)
            );
        }

        AnsiConsole.Write(table);
    }
    
    public void WriteErrorHandling()
    {
        AnsiConsole.Write("Error Handling");

        AnsiConsole.WriteLine("Failure rules specific to a message type");
        AnsiConsole.WriteLine("are applied before the global failure rules");
        AnsiConsole.WriteLine();

        Action<FailureRuleCollection, string> writeTree = (rules, name) =>
        {
            var tree = new Tree(name);

            if (rules.Any())
            {
                foreach (var failure in rules)
                {
                    var node = tree.AddNode(failure.Match.Description);
                    foreach (var slot in failure) node.AddNode(slot.Describe());

                    node.AddNode(new MoveToErrorQueue(new Exception()).ToString());
                }
            }
            else
            {
                var node = tree.AddNode(new AlwaysMatches().Description);
                node.AddNode(new MoveToErrorQueue(new Exception()).ToString());
            }

            AnsiConsole.Write(tree);
        };

        writeTree(_runtime.Options.HandlerGraph.Failures, "Global Failure Rules");

        foreach (var chain in _runtime.Handlers.Chains.Where(x => x.Failures.Any()))
        {
            AnsiConsole.WriteLine();
            writeTree(chain.Failures, $"Message: {chain.MessageType.FullNameInCode()}");
        }
    }
    

    public override ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
    {
        var list = new List<IStatefulResource>();
        
        // These have to run first. Right now, the only options are for building multi-tenanted
        // databases with EF Core
        list.AddRange(_runtime.Services.GetServices<IResourceCreator>());
        
        if (_runtime.Options.ExternalTransportsAreStubbed) return new ValueTask<IReadOnlyList<IStatefulResource>>(list);

        foreach (var transport in _runtime.Options.Transports)
        {
            if (transport.TryBuildStatefulResource(_runtime, out var resource))
            {
                list.Add(resource!);
            }
        }

        if (_runtime.Storage is not NullMessageStore)
        {
            list.Add(new MessageStoreResource(_runtime.Options, _runtime.Storage));
        }

        foreach (var store in _runtime.AncillaryStores)
        {
            list.Add(new MessageStoreResource(_runtime.Options, store));
        }

        return new ValueTask<IReadOnlyList<IStatefulResource>>(list);
    }
}