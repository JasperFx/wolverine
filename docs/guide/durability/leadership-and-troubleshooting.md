# Troubleshooting and Leadership Election

::: info
The main reason to care about this topic is to be able to troubleshoot why messages left stranded by a failed node
are not being recovered in a timely manner
:::

For some technical background, the Wolverine transactional inbox today works through a process of [leadership election](https://en.wikipedia.org/wiki/Leader_election), where only one node 
at any one time is the leader. The recovery of messages from dormant nodes that shut down somehow before they could
finish sending their outgoing or processing all their incoming messages is done through a persistent background agent
assigned to one node by the leader node. 

Long story short, if the message recovery isn't happening very quickly, it's likely some kind of issue with the leadership
election failing to start or to fail over from the previous leader dropping off. 

::: tip
There is no harm in deleting rows from this table. It is strictly a log
:::

As of Wolverine 1.10, there is a table in the PostgreSQL or Sql Server backed message storage called `wolverine_node_records`
that just has a record of detected events relevant to the leader election. All of this information is also logged
through the standard .Net `ILogger`, but it might be easier to understand the data in this table. 

Next, check the `wolverine_nodes` and `wolverine_node_assignments` to see where Wolverine thinks all of the running
agents are across the active nodes. The actual leadership agent is `wolverine://leader`, and you can spot the current
leader by the matching row in the `wolverine_node_assignments` table that refers to the "leader" agent. 

If you are frequently stopping and starting a local process -- especially if you are doing that through a debugger -- you
may want to utilize the `Solo` durability mode explained below:


## Solo Mode

Let's say that you're working on an individual development machine and frequently stopping and starting the application.
You'd ideally like the transactional inbox and outbox processing to kick in fast, but that subsystem has some known hiccups
recovering from exactly the kind of ungraceful process shutdown that happens when developers suddenly kill off the application
running in a debugger. 

To alleviate the issues that developers have had in the past with this mode, Wolverine 1.10 introduced the "Solo" mode
where the system can be optimized to run as if there's never more than one running node:
[..](..%2F..)
<!-- snippet: sample_configuring_the_solo_mode -->
<a id='snippet-sample_configuring_the_solo_mode'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        opts.Services.AddMarten("some connection string")

            // This adds quite a bit of middleware for
            // Marten
            .IntegrateWithWolverine();

        // You want this maybe!
        opts.Policies.AutoApplyTransactions();

        if (context.HostingEnvironment.IsDevelopment())
        {
            // But wait! Optimize Wolverine for usage as
            // if there would never be more than one node running
            opts.Durability.Mode = DurabilityMode.Solo;
        }
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L63-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_solo_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Running your Wolverine application like this means that Wolverine is able to more quickly start the transactional inbox
and outbox at start up time, and also to immediately recover any persisted incoming or outgoing messages from the previous
execution of the service on your local development box.
