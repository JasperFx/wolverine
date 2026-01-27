# Leader Election and Agents

![Who's in charge?](/leader-election.webp)

Wolverine has a couple important features that enable Wolverine to distribute stateful, background work by assigning
running agents to certain running nodes within an application cluster. To do so, Wolverine has a built in [leader election](https://en.wikipedia.org/wiki/Leader_election)
feature so that it can make one single node run a "leadership agent" that continuously ensures that all known and supported
agents are running within the system on a single node.

Here's an illustration of that work distribution:

![Work Distribution across Nodes](/leader-election-diagram.png)

Within Wolverine itself, there are a couple types of "agents" that Wolverine distributes:

1. The ["durability agents"](/guide/durability/) that poll against message stores for any stranded inbox or outbox messages that might need to 
   be recovered and pushed along. Wolverine runs exactly one agent for each message store in the system, and distributes these
   across the cluster
2. "Exclusive Listeners" within Wolverine when you direct Wolverine to only listen to a queue, topic, or message subscription 
   on a single node. This happens when you use the [strictly ordered listening](/guide/messaging/listeners.html#strictly-ordered-listeners) option. 
3. In conjunction with [Marten](https://martendb.io), the [Wolverine managed projection and subscription distribution](/guide/durability/marten/distribution) uses Wolverine's agent assignment
   capability to make sure each projection or subscription is running on exactly one node.

## Enabling Leader Election

Leader election is on by default in Wolverine **if** you have any type of message persistence configured for your 
application and some mechanism for cross node communication. First though, let's talk about message persistence. It could be by PostgreSQL:

<!-- snippet: sample_setup_postgresql_storage -->
<a id='snippet-sample_setup_postgresql_storage'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("postgres");

builder.Host.UseWolverine(opts =>
{
    // Setting up Postgresql-backed message storage
    // This requires a reference to Wolverine.Postgresql
    opts.PersistMessagesWithPostgresql(connectionString);

    // Other Wolverine configuration
});

// This is rebuilding the persistent storage database schema on startup
// and also clearing any persisted envelope state
builder.Host.UseResourceSetupOnStartup();

var app = builder.Build();

// Other ASP.Net Core configuration...

// Using JasperFx opens up command line utilities for managing
// the message storage
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L164-L190' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_postgresql_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or by SQL Server:

<!-- snippet: sample_setup_sqlserver_storage -->
<a id='snippet-sample_setup_sqlserver_storage'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("sqlserver");

builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString);

    // Other Wolverine configuration
});

// This is rebuilding the persistent storage database schema on startup
// and also clearing any persisted envelope state
builder.Host.UseResourceSetupOnStartup();

var app = builder.Build();

// Other ASP.Net Core configuration...

// Using JasperFx opens up command line utilities for managing
// the message storage
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L133-L159' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_sqlserver_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or through the Marten integration:

<!-- snippet: sample_using_the_marten_persistence_integration -->
<a id='snippet-sample_using_the_marten_persistence_integration'></a>
```cs
// Adding Marten
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "orders";
    })

    // Adding the Wolverine integration for Marten.
    .IntegrateWithWolverine();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/OrderEventSourcingSample/Program.cs#L14-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_the_marten_persistence_integration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or by RavenDb:

<!-- snippet: sample_bootstrapping_with_ravendb -->
<a id='snippet-sample_bootstrapping_with_ravendb'></a>
```cs
var builder = Host.CreateApplicationBuilder();

// You'll need a reference to RavenDB.DependencyInjection
// for this one
builder.Services.AddRavenDbDocStore(raven =>
{
    // configure your RavenDb connection here
});

builder.UseWolverine(opts =>
{
    // That's it, nothing more to see here
    opts.UseRavenDbPersistence();
    
    // The RavenDb integration supports basic transactional
    // middleware just fine
    opts.Policies.AutoApplyTransactions();
});

// continue with your bootstrapping...
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/RavenDbTests/DocumentationSamples.cs#L14-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_ravendb' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Next, we need to have some kind of mechanism for cross node communication within Wolverine in the form
of control queues for each node. When Wolverine bootstraps, it uses the message persistence to save
information about the new node including a `Uri` for a control endpoint where other Wolverine nodes should
send messages to "control" agent assignments. 

If you're using any of the message persistence options above, there's a fallback mechanism using the associated
databases to act as a simplistic message queue between nodes. For better results though, some of the transports in Wolverine
can instead use a non-durable queue for each node that will probably provide for better results. At the time this guide
was written, the [Rabbit MQ transport](/guide/messaging/transports/rabbitmq/) and the [Azure Service Bus transport](/guide/messaging/transports/azureservicebus/) support this feature. 

## Disabling Leader Election

If you want to disable leader election and all the cross node traffic, or maybe if you just want to optimize automated 
testing scenarios by making a newly launched process automatically start up all possible agents immediately, you can use
the `DurabilityMode.Solo` setting as shown below:

<!-- snippet: sample_configuring_the_solo_mode -->
<a id='snippet-sample_configuring_the_solo_mode'></a>
```cs
var builder = Host.CreateApplicationBuilder();

builder.UseWolverine(opts =>
{
    opts.Services.AddMarten("some connection string")

        // This adds quite a bit of middleware for
        // Marten
        .IntegrateWithWolverine();

    // You want this maybe!
    opts.Policies.AutoApplyTransactions();

    if (builder.Environment.IsDevelopment())
    {
        // But wait! Optimize Wolverine for usage as
        // if there would never be more than one node running
        opts.Durability.Mode = DurabilityMode.Solo;
    }
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L55-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_solo_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For testing, you also have this helper:

<!-- snippet: sample_using_run_wolverine_in_solo_mode_with_extension -->
<a id='snippet-sample_using_run_wolverine_in_solo_mode_with_extension'></a>
```cs
// This is bootstrapping the actual application using
// its implied Program.Main() set up
// For non-Alba users, this is using IWebHostBuilder 
Host = await AlbaHost.For<WolverineWebApi.Program>(x =>
{
    x.ConfigureServices(services =>
    {
        // Override the Wolverine configuration in the application
        // to run the application in "solo" mode for faster
        // testing cold starts
        services.RunWolverineInSoloMode();

        // And just for completion, disable all Wolverine external 
        // messaging transports
        services.DisableAllExternalWolverineTransports();
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/IntegrationContext.cs#L27-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_run_wolverine_in_solo_mode_with_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Likewise, any other `DurabilityMode` setting than `Balanced` (the default) will
disable leader election.

## Writing Your Own Agent Family

To write your own family of "sticky" agents and use Wolverine to distribute them across an application cluster,
you'll first need to make implementations of this interface:

<!-- snippet: sample_IAgent -->
<a id='snippet-sample_iagent'></a>
```cs
/// <summary>
///     Models a constantly running background process within a Wolverine
///     node cluster
/// </summary>
public interface IAgent : IHostedService // Standard .NET interface for background services
{
    /// <summary>
    ///     Unique identification for this agent within the Wolverine system
    /// </summary>
    Uri Uri { get; }
    
    // Not really used for anything real *yet*, but 
    // hopefully becomes something useful for CritterWatch
    // health monitoring
    AgentStatus Status { get; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Agents/IAgent.cs#L9-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iagent' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_iagent-1'></a>
```cs
/// <summary>
///     Models a constantly running background process within a Wolverine
///     node cluster
/// </summary>
public interface IAgent : IHostedService // Standard .NET interface for background services
{
    /// <summary>
    ///     Unique identification for this agent within the Wolverine system
    /// </summary>
    Uri Uri { get; }
    
    // Not really used for anything real *yet*, but 
    // hopefully becomes something useful for CritterWatch
    // health monitoring
    AgentStatus Status { get; }
}

public class CompositeAgent : IAgent
{
    private readonly List<IAgent> _agents;
    public Uri Uri { get; }

    public CompositeAgent(Uri uri, IEnumerable<IAgent> agents)
    {
        Uri = uri;
        _agents = agents.ToList();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StartAsync(cancellationToken);
        }

        Status = AgentStatus.Running;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StopAsync(cancellationToken);
        }

        Status = AgentStatus.Running ;
    }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Agents/IAgent.cs#L7-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iagent-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that you could use [BackgroundService](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-9.0&tabs=visual-studio) as a base class. 

The `Uri` property just needs to be unique and match up with our next service interface. Wolverine uses that `Uri` as a
unique identifier to track where and whether the known agents are executing.

The next service is the actual distributor. To plug into Wolverine, you need to build an implementation of this service:

<!-- snippet: sample_IAgentFamily -->
<a id='snippet-sample_iagentfamily'></a>
```cs
/// <summary>
///     Pluggable model for managing the assignment and execution of stateful, "sticky"
///     background agents on the various nodes of a running Wolverine cluster
/// </summary>
public interface IAgentFamily
{
    /// <summary>
    ///     Uri scheme for this family of agents
    /// </summary>
    string Scheme { get; }

    /// <summary>
    ///     List of all the possible agents by their identity for this family of agents
    /// </summary>
    /// <returns></returns>
    ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync();

    /// <summary>
    ///     Create or resolve the agent for this family
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="wolverineRuntime"></param>
    /// <returns></returns>
    ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime);

    /// <summary>
    ///     All supported agent uris by this node instance
    /// </summary>
    /// <returns></returns>
    ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync();

    /// <summary>
    ///     Assign agents to the currently running nodes when new nodes are detected or existing
    ///     nodes are deactivated
    /// </summary>
    /// <param name="assignments"></param>
    /// <returns></returns>
    ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Agents/IAgentFamily.cs#L16-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iagentfamily' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this case, you can plug custom `IAgentFamily` strategies into Wolverine by just registering a concrete service in 
your DI container against that `IAgentFamily` interface (`services.AddSingleton<IAgentFamily, MySpecialAgentFamily>();`). 
Wolverine does a simple `IServiceProvider.GetServices<IAgentFamily>()` during its bootstrapping to find them.

As you can probably guess, the `Scheme` should be unique, and the `Uri` structure needs to be unique across all of your agents. 
`EvaluateAssignmentsAsync()` is your hook to create distribution strategies, with a simple “just distribute these things evenly across my cluster” 
strategy possible like this example from Wolverine itself:

```csharp
public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
{
    assignments.DistributeEvenly(Scheme);
    return ValueTask.CompletedTask;
}
```
If you go looking for it, the equivalent in Wolverine’s distribution of Marten projections and subscriptions is a 
tiny bit more complicated in that it uses knowledge of node capabilities to support blue/green semantics to 
only distribute work to the servers that “know” how to use particular agents 
(like version 3 of a projection that doesn’t exist on “blue” nodes):

```csharp
public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
{
    assignments.DistributeEvenlyWithBlueGreenSemantics(SchemeName);
    return new ValueTask();
}
```

The `AssignmentGrid` tells you the current state of your application in terms of which node is the leader, what 
all the currently running nodes are, and which agents are running on which nodes. Beyond the even distribution, 
the `AssignmentGrid` has fine grained API methods to start, stop, or reassign individual agents to specific running nodes.

To wrap this up, I’m trying to guess at the questions you might have and see if I can cover all the bases:

* **Is some kind of persistence necessary?** Yes, absolutely. Wolverine has to have some way to “know” what nodes are running and which agents are really running on each node.
* **How does Wolverine do health checks for each node?** If you look in the wolverine_nodes table when using PostgreSQL or Sql Server, you’ll see a heartbeat column with a timestamp. Each Wolverine application is running a polling operation that updates its heartbeat timestamp and also checks that there is a known leader node. In normal shutdown, Wolverine tries to gracefully mark the current node as offline and send a message to the current leader node if there is one telling the leader that the node is shutting down. In real world usage though, Kubernetes or who knows what is frequently killing processes without a clean shutdown. In that case, the leader node will be able to detect stale nodes that are offline, eject them from the node persistence, and redistribute agents.
* **Can Wolverine switch over the leadership role?** Yes, and that should be relatively quick. Plus Wolverine would keep trying to start a leader election if none is found. But yet, it’s an imperfect world where things can go wrong and there will 100% be the ability to either kickstart or assign the leader role from the forthcoming CritterWatch user interface.
* **How does the leadership election work?** Crudely and relatively effectively. All of the storage mechanics today have some kind of sequential node number assignment for all newly persisted nodes. In a kind of simplified “Bully Algorithm,” Wolverine will always try to send “try assume leadership” messages to the node with the lowest sequential node number which will always be the longest running node. When a node does try to take leadership, it uses whatever kind of global, advisory lock function the current persistence uses to get sole access to write the leader node assignment to itself, but will back out if the current node detects from storage that the leadership is already running on another active node.










