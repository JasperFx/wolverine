using System.Text;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine;

public class WolverineMessageExpectationException : Exception
{
    public WolverineMessageExpectationException(string message, IReadOnlyList<object> messages) : base(message)
    {
        Messages = messages.ToArray();
    }

    public IReadOnlyList<object> Messages { get; }
}

public static class TestingExtensions
{
    private static object toMessage(this object message)
    {
        if (message is Envelope e)
        {
            return e.Message!;
        }

        return message;
    }

    private static object[] resolveMessages(this IEnumerable<object> messages)
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        return messages.Select(x => x.toMessage()).Where(x => x != null)
            .ToArray();
    }

    private static string toListOfMessages(this IEnumerable<object> messages)
    {
        var actual = messages
            .resolveMessages();

        if (!actual.Any())
        {
            return "[no messages]";
        }

        return actual.Select(x => x.ToString()!).Join(", ");
    }

    /// <summary>
    ///     Find the first envelope of the specified message type. Will throw if
    ///     no matching envelope is found
    /// </summary>
    /// <param name="envelopes"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static Envelope FindForMessageType<T>(this IEnumerable<Envelope> envelopes)
    {
        var values = envelopes.ToArray();
        return values.FirstOrDefault(x => x.Message is T) ?? throw new WolverineMessageExpectationException(
            $"Could not find an envelope with message type {typeof(T).FullNameInCode()}. The actual messages were {values.toListOfMessages()}",
            values.resolveMessages());
    }

    /// <summary>
    ///     Assert that there are no messages of any type within this published collection
    /// </summary>
    /// <param name="messages"></param>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static void ShouldHaveNoMessages(this IEnumerable<object>? messages)
    {
        if (messages == null)
        {
            return;
        }

        // ReSharper disable once PossibleMultipleEnumeration
        if (messages.Any())
        {
            throw new WolverineMessageExpectationException(
                $"Should be no messages, but was {messages.toListOfMessages()}", messages.ToArray());
        }
    }

    /// <summary>
    ///     Assert that no messages of type T were part of this collection
    /// </summary>
    /// <param name="messages"></param>
    /// <typeparam name="T"></typeparam>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static void ShouldHaveNoMessageOfType<T>(this IEnumerable<object> messages)
    {
        var actual = messages.resolveMessages();
        if (actual.Any(message => message is T or DeliveryMessage<T>))
        {
            throw new WolverineMessageExpectationException(
                $"Should be no messages of type {typeof(T).FullNameInCode()}, but the actual messages were {actual.toListOfMessages()}",
                actual);
        }
    }

    /// <summary>
    ///     Assert and return the first message of type T within this collection
    ///     of published messages (unwraps DeliveryMessage<T>.Message if necessary)
    /// </summary>
    /// <param name="messages"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static T ShouldHaveMessageOfType<T>(this IEnumerable<object> messages)
    {
        return ShouldHaveMessageOfType<T>(messages, null);
    }

    /// <summary>
    ///     Assert and return the first message of type T within this collection
    ///     of published messages (unwraps DeliveryMessage<T>.Message if necessary)
    /// </summary>
    /// <param name="messages"></param>
    /// <param name="deliveryAssertions">
    ///     Optional assertions against the DeliveryOptions the message was published
    ///     with. If the message was not published with DeliveryOptions, null is supplied
    ///     to this action.
    /// </param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static T ShouldHaveMessageOfType<T>(this IEnumerable<object> messages, Action<DeliveryOptions?>? deliveryAssertions)
    {
        var actual = messages.resolveMessages();
        if (!actual.Any())
        {
            throw new WolverineMessageExpectationException(
                $"Should be a message of type {typeof(T).FullNameInCode()}, but there were no messages", actual);
        }

        foreach (var message in actual)
        {
            if (message is T directMatch)
            {
                deliveryAssertions?.Invoke(null);
                return directMatch;
            }
            if (message is DeliveryMessage<T> deliveryMessage)
            {
                deliveryAssertions?.Invoke(deliveryMessage.Options);
                return deliveryMessage.Message;
            }
        }

        throw new WolverineMessageExpectationException(
            $"Should be a message of type {typeof(T).FullNameInCode()}, but actual messages were {actual.toListOfMessages()}",
            actual);
    }

    /// <summary>
    ///     If it exists, find the first envelope that contains a message of type T
    ///     within this collection of published messages. This is only necessary for
    ///     testing customized message sending (explicit destination, headers, scheduled delivery)
    /// </summary>
    /// <param name="messages"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="WolverineMessageExpectationException"></exception>
    public static Envelope ShouldHaveEnvelopeForMessageType<T>(this IEnumerable<object> messages)
    {
        return messages.OfType<Envelope>()
            .FirstOrDefault(x => x.Message is T) ?? throw new WolverineMessageExpectationException(
            $"Unable to find an envelope for type {typeof(T).FullNameInCode()}, actual messages were {messages.toListOfMessages()}",
            messages.ToArray());
    }
    
    /// <summary>
    /// Test helper for agent assignment within Wolverine. Definitely an advanced usage!
    /// </summary>
    /// <param name="expectedLeader"></param>
    /// <param name="configure"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    public static Task<bool> WaitUntilAssignmentsChangeTo(this IHost expectedLeader,
        Action<AssignmentWaiter> configure, TimeSpan timeout)
    {
        var waiter = new AssignmentWaiter(expectedLeader);
        configure(waiter);

        return waiter.Start(timeout);
    }

    /// <summary>
    /// Test helper to quickly retrieve the identity list of all running agents
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    public static IReadOnlyList<Uri> RunningAgents(this IHost host)
    {
        return host.GetRuntime().Agents.AllRunningAgentUris();
    }
    
    public class AssignmentWaiter : IObserver<IWolverineEvent>
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


    
    // // Used internally by the method above
    // public class AssignmentWaiter
    // {
    //     public Dictionary<Guid, int> AgentCountByHost { get; } = new();
    //     private readonly Dictionary<Guid, IAgentRuntime> _runtimes = new();
    //     private readonly WolverineRuntime _leaderRuntime;
    //
    //     public string AgentScheme { get; set; }
    //
    //     public AssignmentWaiter(IHost leader)
    //     {
    //         var runtime = leader.GetRuntime();
    //         _leaderRuntime = runtime;
    //         _runtimes[runtime.Options.UniqueNodeId] = runtime.Agents;
    //     }
    //
    //     public void ExpectRunningAgents(IHost host, int runningCount)
    //     {
    //         var runtime = host.GetRuntime();
    //         var id = runtime.Options.UniqueNodeId;
    //         AgentCountByHost[id] = runningCount;
    //         _runtimes[id] = runtime.Agents;
    //     }
    //
    //     public Task<bool> Start(TimeSpan timeout)
    //     {
    //         if (HasReached()) return Task.FromResult(true);
    //
    //         var timeout1 = new CancellationTokenSource(timeout);
    //         timeout1.CancelAfter(timeout);
    //         return Task.Factory.StartNew(async () =>
    //         {
    //             try
    //             {
    //                 while (!timeout1.IsCancellationRequested)
    //                 {
    //                     if (HasReached()) return true;
    //                     await Task.Delay(1.Seconds(), timeout1.Token);
    //                 }
    //
    //                 if (HasReached()) return true;
    //
    //                 var builder = await writePersistedActualsAsync();
    //
    //                 throw new TimeoutException(builder.ToString());
    //             }
    //             catch (TaskCanceledException)
    //             {
    //                 if (HasReached()) return true;
    //                 
    //                 var builder = await writePersistedActualsAsync();
    //
    //                 throw new TimeoutException(builder.ToString());
    //             }
    //         }, timeout1.Token).Unwrap();
    //     }
    //
    //     private async Task<StringBuilder> writePersistedActualsAsync()
    //     {
    //         var nodes = await _leaderRuntime.Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);
    //
    //         var builder = new StringBuilder();
    //         var writer = new StringWriter(builder);
    //         
    //         writer.WriteLine("According to the database...");
    //             
    //         foreach (var node in nodes.OrderBy(x => x.AssignedNodeId))
    //         {
    //             writer.WriteLine($"Node {node.AssignedNodeId} is running:");
    //             foreach (var uri in node.ActiveAgents.OrderBy(x => x.ToString()))
    //             {
    //                 writer.WriteLine(uri);
    //             }
    //         }
    //         
    //         writer.WriteLine();
    //         writer.WriteLine("According to the runtimes");
    //         foreach (var node in nodes.OrderBy(x => x.AssignedNodeId))
    //         {
    //             writer.WriteLine($"Node {node.AssignedNodeId} is running:");
    //             var runtime = _runtimes[node.Id];
    //             
    //             foreach (var uri in runtime.AllRunningAgentUris().OrderBy(x => x.ToString()))
    //             {
    //                 writer.WriteLine(uri);
    //             }
    //         }
    //
    //         return builder;
    //     }
    //
    //     public bool HasReached()
    //     {
    //         Func<Uri, bool> filter = AgentScheme.IsEmpty()
    //             ? x => !x.Scheme.StartsWith("wolverine")
    //             : x => x.Scheme.EqualsIgnoreCase(AgentScheme);
    //         
    //         foreach (var pair in AgentCountByHost)
    //         {
    //             var runtime = _runtimes[pair.Key];
    //
    //             var runningCount = runtime.AllRunningAgentUris().Count(x => filter(x));
    //             if (pair.Value != runningCount) return false;
    //         }
    //
    //         return true;
    //     }
    //
    // }
}

