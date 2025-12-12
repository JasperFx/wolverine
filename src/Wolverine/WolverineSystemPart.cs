using System.Reflection;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine;

internal class WolverineSystemPart : SystemPartBase
{
    public static bool WithinDescription = false;
    
    private readonly WolverineRuntime _runtime;

    public WolverineSystemPart(IWolverineRuntime runtime) : base("Wolverine", new Uri("wolverine://" + runtime.Options.ServiceName))
    {
        _runtime = (WolverineRuntime)runtime;
    }

    public override async Task WriteToConsole()
    {
        WithinDescription = true;
        await _runtime.StartLightweightAsync();
        
        _runtime.Options.WriteToConsole();
        
        AnsiConsole.WriteLine();
        
        await _runtime.Options.HandlerGraph.WriteToConsole();
        AnsiConsole.WriteLine();
        WriteMessageSubscriptions();
        AnsiConsole.WriteLine();
        WriteSendingEndpoints();
        AnsiConsole.WriteLine();
        WriteListeners();
        AnsiConsole.WriteLine();
        WriteErrorHandling();
    }
    
    public void WriteMessageSubscriptions()
    {
        var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);

        if (!messageTypes.Any())
        {
            AnsiConsole.Markup("[gray]No message types found");
            return;
        }

        var table = new Table(){Title = new TableTitle("Message Routing")
        {
            Style = new Style(decoration:Decoration.Bold)
        }}.AddColumns(".NET Type", "Message Type Alias", "Destination", "Content Type");
        foreach (var messageType in messageTypes.Where(x => x.Assembly != Assembly.GetExecutingAssembly()).OrderBy(x => x.FullName))
        {
            var routes = _runtime.RoutingFor(messageType).Routes;
            if (routes.Any())
            {
                foreach (var route in routes.OfType<MessageRoute>())
                {
                    table.AddRow(messageType.FullNameInCode().EscapeMarkup(), messageType.ToMessageTypeName().EscapeMarkup(), route.Uri.ToString().EscapeMarkup(),
                        route.Serializer?.ContentType.EscapeMarkup() ?? "application/json");
                }
            }
            else
            {
                table.AddRow(messageType.FullNameInCode().EscapeMarkup(), messageType.ToMessageTypeName().EscapeMarkup(), "No Routes",
                    "n/a".EscapeMarkup());
            }
            

        }

        AnsiConsole.Write(table);
    }

    public void WriteSendingEndpoints()
    {
        // This just forces Wolverine to go find and build any extra sender agents
        var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);
        foreach (var messageType in messageTypes) _runtime.RoutingFor(messageType);


        var table = new Table(){Title = new TableTitle("Subscriptions"){Style = new Style(decoration:Decoration.Bold)}};

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
        var table = new Table(){Title = new TableTitle("Listeners"){Style = new Style(decoration:Decoration.Bold)}};

        table.AddColumn("Uri");
        table.AddColumn("Name");
        table.AddColumn("Mode");
        table.AddColumn(nameof(Endpoint.MaxDegreeOfParallelism));
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
                listener.MaxDegreeOfParallelism.ToString(),
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
    

    public override async ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
    {
        WithinDescription = true;

        try
        {
            var list = new List<IStatefulResource>();
        
            // These have to run first. Right now, the only options are for building multi-tenanted
            // databases with EF Core
            list.AddRange(_runtime.Services.GetServices<IResourceCreator>());

            if (!_runtime.Options.ExternalTransportsAreStubbed)
            {
                foreach (var transport in _runtime.Options.Transports)
                {
                    if (transport.TryBuildStatefulResource(_runtime, out var resource))
                    {
                        await transport.InitializeAsync(_runtime);
                        list.Add(resource!);
                    }
                }
            
                // Force Wolverine to find all message types...
                var messageTypes = _runtime.Options.Discovery.FindAllMessages(_runtime.Options.HandlerGraph);
            
                // ...and force Wolverine to *also* execute the routing, which
                // may discover new endpoints
                foreach (var messageType in messageTypes.Where(x => x.Assembly != GetType().Assembly))
                {
                    _runtime.RoutingFor(messageType);
                }
            }

            var stores = await _runtime.Stores.FindAllAsync();
        
            list.AddRange(stores.Select(store => new MessageStoreResource(_runtime.Options, store)));

            return list;
        }
        finally
        {
            WithinDescription = false;
        }
    }
}