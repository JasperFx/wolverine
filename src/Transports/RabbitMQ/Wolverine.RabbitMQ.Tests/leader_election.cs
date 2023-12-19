using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Oakton.Resources;
using Shouldly;
using Weasel.Postgresql;
using Wolverine.Logging;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class leader_election : RabbitMQContext,IAsyncLifetime
{
    private readonly List<IHost> _hosts = new();
    private readonly ITestOutputHelper _output;
    private IHost _originalHost;

    public leader_election(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await dropSchema();

        _originalHost = await startHostAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.StopAsync();
    }

    private async Task<IHost> startHostAsync()
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAgentFamily, FakeAgentFamily>();
                opts.UseRabbitMq().EnableWolverineControlQueues();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "registry");
                opts.Services.AddSingleton<ILoggerProvider>(new OutputLoggerProvider(_output));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        new XUnitEventObserver(host, _output);

        _hosts.Add(host);

        return host;
    }

    private async Task shutdownHostAsync(IHost host)
    {
        await host.StopAsync();
        _hosts.Remove(host);
    }

    private static async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("registry");
        await conn.CloseAsync();
    }

    [Fact]
    public async Task the_only_known_node_is_automatically_the_leader()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(10.Seconds());
        tracker.IsLeader().ShouldBeTrue();
    }

    /***** NEW TESTS START HERE **********************************************/

    private bool allAgentsAreRunning(WolverineTracker tracker)
    {
        var agents = FakeAgentFamily.AllAgentUris();
        return agents.All(tracker.AgentIsRunning);
    }

    [Fact]
    public async Task the_original_node_knows_about_all_the_possible_agents()
    {
        await _originalHost.WaitUntilAssignmentsChangeTo(w => w.ExpectRunningAgents(_originalHost, 12), 10.Seconds());
    }

    [Fact]
    public async Task add_second_node_see_balanced_nodes()
    {
        var tracker = _originalHost.GetRuntime().Tracker;

        var host2 = await startHostAsync();

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 6);
            w.ExpectRunningAgents(host2, 6);
        }, 10.Seconds());
    }


    /***** NEW TESTS END HERE **********************************************/

    [Fact]
    public async Task send_node_event_for_starting_on_startup()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var waiter = _originalHost.GetRuntime().Tracker.WaitForNodeEvent(NodeEventType.Started, 10.Seconds());

        var host2 = await startHostAsync();

        var @event = await waiter;

        var runtime2 = host2.GetRuntime();
        @event.Node.Id.ShouldBe(runtime2.Options.UniqueNodeId);

        _originalHost.GetRuntime().Tracker.Nodes.ContainsKey(runtime2.Options.UniqueNodeId).ShouldBeTrue();

        // Should not take over leadership
        runtime2.Tracker.IsLeader().ShouldBeFalse();
        _originalHost.GetRuntime().Tracker.IsLeader().ShouldBeTrue();
    }

    [Fact]
    public async Task send_node_event_for_exiting_node()
    {
        var host2 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 6);
            w.ExpectRunningAgents(host2, 6);
        }, 10.Seconds());

        var waiter = _originalHost.GetRuntime().Tracker.WaitForNodeEvent(NodeEventType.Exiting, 10.Seconds());
        var host2Id = host2.GetRuntime().Options.UniqueNodeId;

        await shutdownHostAsync(host2);

        var @event = await waiter;

        @event.Node.Id.ShouldBe(host2Id);

        _originalHost.GetRuntime().Tracker.Nodes.Count.ShouldBe(1);

        _originalHost.GetRuntime().Tracker.Nodes.ContainsKey(host2Id).ShouldBeFalse();
    }

    [Fact]
    public async Task leader_switchover_between_nodes()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        await _originalHost.StopAsync();

        await host2.GetRuntime().Tracker.WaitUntilAssumesLeadershipAsync(15.Seconds());

        await host2.StopAsync();

        await host3.GetRuntime().Tracker.WaitUntilAssumesLeadershipAsync(30.Seconds());
    }

    [Fact]
    public async Task spin_up_several_nodes_take_away_original_node()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        await _originalHost.StopAsync();

        await host2.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }
    
    [Fact]
    public async Task spin_up_several_nodes_take_away_non_leader_node()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        await host3.StopAsync();

        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(_originalHost, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }

    [Fact]
    public async Task verify_assignments_can_make_corrections()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        var runtime4 = host4.GetRuntime();
        foreach (var agentUri in runtime4.Agents.AllRunningAgentUris())
        {
            await runtime4.Agents.StopLocallyAsync(agentUri);
        }

        // This should eventually turn back on the missing agents from node4
        await _originalHost.InvokeMessageAndWaitAsync(new VerifyAssignments());
        
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());
    }
    
    [Fact]
    public async Task eject_a_stale_node()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        var runtime4 = host4.GetRuntime();

        await runtime4.DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));

        await _originalHost.InvokeMessageAndWaitAsync(new CheckAgentHealth());
        
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 4);
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
        }, 30.Seconds());
    }
    
        
    [Fact]
    public async Task take_over_leader_ship_if_leader_becomes_stale()
    {
        var tracker = _originalHost.GetRuntime().Tracker;
        await tracker.WaitUntilAssumesLeadershipAsync(5.Seconds());

        var host2 = await startHostAsync();
        var host3 = await startHostAsync();
        var host4 = await startHostAsync();

        // This is just to eliminate some errors in test output
        await _originalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(_originalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());

        await _originalHost.GetRuntime().DisableAgentsAsync(DateTimeOffset.UtcNow.AddHours(-1));
        
        var runtime2 = host2.GetRuntime();
        await host2.InvokeMessageAndWaitAsync(new CheckAgentHealth());
        await runtime2.Tracker.WaitUntilAssumesLeadershipAsync(15.Seconds());

        
        await host2.WaitUntilAssignmentsChangeTo(w =>
        {
            w.ExpectRunningAgents(host2, 4);
            w.ExpectRunningAgents(host3, 4);
            w.ExpectRunningAgents(host4, 4);
        }, 30.Seconds());
    }
    
    [Fact]
    public async Task persist_and_load_node_records()
    {
        var records = new NodeRecord[]
        {
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStarted
            },
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStopped
            },
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 2,
                RecordType = NodeRecordType.NodeStarted
            },
            new NodeRecord
            {
                Description = "one",
                NodeNumber = 2,
                RecordType = NodeRecordType.NodeStopped
            },
        };

        var runtime = _originalHost.GetRuntime();

        var nodes = runtime.Storage.Nodes;
        await nodes.LogRecordsAsync(records);

        var count = 0;
        while (count < 10)
        {
            var persisted = await nodes.FetchRecentRecordsAsync(10);
            if (persisted.Count >= 4) return;
        }

        throw new Exception("No persisted node records!");
    }
}

public class FakeAgentFamily : IAgentFamily
{
    public string Scheme { get; } = "fake";

    public static string[] Names = new string[]
    {
        "one",
        "two",
        "three",
        "four",
        "five",
        "six",
        "seven",
        "eight",
        "nine",
        "ten",
        "eleven",
        "twelve"
    };

    public LightweightCache<Uri, FakeAgent> Agents { get; } = new(x => new FakeAgent(x));
    
    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var agents = Names.Select(x => new Uri($"fake://{x}")).ToArray();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime runtime)
    {
        return new ValueTask<IAgent>(Agents[uri]);
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var agents = AllAgentUris();
        return ValueTask.FromResult((IReadOnlyList<Uri>)agents);
    }

    public static Uri[] AllAgentUris()
    {
        return Names.Select(x => new Uri($"fake://{x}")).ToArray();
    }
}

public class FakeAgent : IAgent
{
    public FakeAgent(Uri uri)
    {
        Uri = uri;
    }
    
    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public Uri Uri { get; }
}

internal static class HostExtensions
{
    internal static Task<bool> WaitUntilAssignmentsChangeTo(this IHost expectedLeader,
        Action<AssignmentWaiter> configure, TimeSpan timeout)
    {
        var waiter = new AssignmentWaiter(expectedLeader);
        configure(waiter);

        return waiter.Start(timeout);
    }
}

internal class AssignmentWaiter : IObserver<IWolverineEvent>
{
    private readonly TaskCompletionSource<bool> _completion = new();
    
    private IDisposable _unsubscribe;
    private readonly WolverineTracker _tracker;

    public Dictionary<Guid, int> AgentCountByHost { get; } = new();
    public string AgentScheme { get; set; }

    public AssignmentWaiter(IHost leader)
    {
        _tracker = leader.GetRuntime().Tracker;
    }

    public void ExpectRunningAgents(IHost host, int runningCount)
    {
        var id = host.GetRuntime().Options.UniqueNodeId;
        AgentCountByHost[id] = runningCount;
    }

    public Task<bool> Start(TimeSpan timeout)
    {
        if (HasReached()) return Task.FromResult(true);
        
        _unsubscribe = _tracker.Subscribe(this);
        
        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                "Did not reach the expected state or message in time"));
        });


        return _completion.Task;
    }

    public bool HasReached()
    {
        foreach (var pair in AgentCountByHost)
        {
            Func<Uri, bool> filter = AgentScheme.IsEmpty()
                ? x => !x.Scheme.StartsWith("wolverine")
                : x => x.Scheme.EqualsIgnoreCase(AgentScheme);
            
            var runningCount = _tracker.Agents.ToArray().Where(x => filter(x.Key)).Count(x => x.Value == pair.Key);
            if (pair.Value != runningCount) return false;
        }

        return true;
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
        _completion.SetException(error);
    }

    public void OnNext(IWolverineEvent value)
    {
        if (HasReached())
        {
            _completion.TrySetResult(true);
            _unsubscribe.Dispose();
        }
    }
}

public class OutputLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public OutputLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }


    public void Dispose()
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(_output, categoryName);
    }
}

public class XUnitLogger : ILogger
{
    private readonly string _categoryName;

    private readonly List<string> _ignoredStrings = new()
    {
        "Declared",
        "Successfully processed message"
    };

    private readonly ITestOutputHelper _testOutputHelper;

    public XUnitLogger(ITestOutputHelper testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return new Disposable();
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (exception is DivideByZeroException)
        {
            return;
        }

        if (exception is BadImageFormatException)
        {
            return;
        }

        // if (_categoryName == "Wolverine.Runtime.WolverineRuntime" &&
        //     logLevel == LogLevel.Information) return;


        var text = formatter(state, exception);
        //if (_ignoredStrings.Any(x => text.Contains(x))) return;

        _testOutputHelper.WriteLine($"{_categoryName}/{logLevel}: {text}");

        if (exception != null)
        {
            _testOutputHelper.WriteLine(exception.ToString());
        }
    }

    public class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}



public class XUnitEventObserver : IObserver<IWolverineEvent>
{
    private readonly ITestOutputHelper _output;
    private readonly int _assignedId;

    public XUnitEventObserver(IHost host, ITestOutputHelper output)
    {
        _output = output;
        var runtime = host.GetRuntime();

        _assignedId = runtime.Options.Durability.AssignedNodeNumber;

        runtime.Tracker.Subscribe(this);
    }

    public void OnCompleted()
    {
        
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(IWolverineEvent value)
    {
        if (value is AgentAssignmentsChanged changed)
        {
            _output.WriteLine($"Host {_assignedId}: Agent assignments determined for known nodes {changed.Assignments.Nodes.Select(x => x.ToString()).Join(", ")}");
            if (!changed.Commands.Any()) _output.WriteLine("No assignment changes detected");

            foreach (var agent in changed.Assignments.AllAgents)
            {
                if (agent.AssignedNode == null)
                {
                    _output.WriteLine($"* {agent.Uri} is not assigned");
                }
                else if (agent.OriginalNode == null)
                {
                    _output.WriteLine($"* {agent.Uri} assigned to node {agent.AssignedNode.AssignedId}");
                }
                else if (agent.OriginalNode == agent.AssignedNode)
                {
                    _output.WriteLine($"* {agent.Uri} is unchanged on node {agent.AssignedNode.AssignedId}");
                }
                else
                {
                    _output.WriteLine($"* {agent.Uri} reassigned from node {agent.OriginalNode.AssignedId} to node {agent.AssignedNode.AssignedId}");
                }
            }
        }
        else
        {
            _output.WriteLine($"Host {_assignedId}: {value}");
        }
    }
}