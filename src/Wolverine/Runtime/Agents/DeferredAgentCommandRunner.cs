using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Wolverine.Util;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Marks a batched agent command whose <b>remote</b> execution is taken off the receiving node's
///     control lane. See <see cref="DeferredAgentCommandRunner" />.
/// </summary>
internal interface IDeferredAgentWork : IAgentCommand
{
}

/// <summary>
///     GH-3748: executes remotely-received batched agent commands (<see cref="StartAgents" /> /
///     <see cref="StopAgents" />) on a background queue instead of inline on the control endpoint's
///     message lane, then sends the correlated typed reply when the work actually finishes.
///
///     <para>The database control endpoint is deliberately serial (<c>MaxDegreeOfParallelism = 1</c>), so
///     before this a chunk of slow agent starts — a Marten projection shard replaying behind a version
///     bump was measured at 27s p50 / 215s max <i>per shard</i> — monopolized the node's entire control
///     lane for however long the batch took. Every other control message to that node sat behind it:
///     stops, reassignment stops, and any request that could have told the leader the node was making
///     progress. The command is accepted here and the lane is free in microseconds.</para>
///
///     <para>The reply is not sent early: it still goes out only when the batch has genuinely finished,
///     carrying exactly the confirmed agents, and it is correlated to the original request's conversation
///     so a leader blocked in <c>InvokeAsync&lt;AgentsStarted&gt;</c> — including a pre-GH-3748 leader
///     during a rolling upgrade — completes exactly as if the work had run inline. What changes is only
///     <i>where</i> the work runs, which is what lets the leader ask <see cref="QueryAgentPresence" />
///     for progress while the batch is still going.</para>
///
///     <para>Work items run strictly one at a time in arrival order, preserving the ordering the serial
///     control lane used to give consecutive batched commands, and capping a node's agent-start
///     parallelism at one batch's worth (<c>MaxAgentStartParallelism</c>) no matter how many chunks are
///     queued.</para>
/// </summary>
internal class DeferredAgentCommandRunner : IAsyncDisposable
{
    private readonly record struct Item(IAgentCommand Command, string ReplyRequested, Uri ReplyUri, Guid ConversationId, long Sequence);

    private readonly WolverineRuntime _runtime;
    private readonly NodeAgentController _controller;
    private readonly CancellationToken _cancellation;
    private readonly Task _worker;

    private readonly Channel<Item> _channel =
        Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });

    public DeferredAgentCommandRunner(WolverineRuntime runtime, NodeAgentController controller,
        CancellationToken cancellation)
    {
        _runtime = runtime;
        _controller = controller;
        _cancellation = cancellation;

        _worker = Task.Run(runAsync, CancellationToken.None);
    }

    /// <summary>
    ///     Accept a remotely-received batched command for background execution. False only when the
    ///     runner is shutting down, in which case the caller should fall back to inline execution.
    /// </summary>
    public bool TryEnqueue(IAgentCommand command, string replyRequested, Uri replyUri, Guid conversationId)
    {
        // The sequence is captured at ACCEPTANCE, not execution: a stop command that arrives while this
        // batch is still queued must revoke the batch's starts for its agent, exactly as it would have
        // executed after them on the old serial lane. See NodeAgentController.StartAgentGuardedAsync.
        var item = new Item(command, replyRequested, replyUri, conversationId, _controller.CurrentCommandSequence);

        return _channel.Writer.TryWrite(item);
    }

    private async Task runAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            Item item;
            try
            {
                item = await _channel.Reader.ReadAsync(_cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                var results = await executeAsync(item);

                foreach (var result in results)
                {
                    if (result.GetType().ToMessageTypeName() == item.ReplyRequested)
                    {
                        await _runtime.SendAgentCommandReplyAsync(item.ReplyUri, item.ConversationId, result);
                    }
                    else
                    {
                        // Nothing produces these today; surfaced rather than silently dropped in case a
                        // future command type does.
                        _runtime.Logger.LogInformation(
                            "Discarding cascaded agent command {Command} from deferred execution of {Original}",
                            result, item.Command);
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Cost this one batch only. The requester's reply window or progress polling decides
                // what to do about the silence.
                _runtime.Logger.LogError(e, "Error executing deferred agent command {Command}", item.Command);
            }
        }
    }

    private async Task<AgentCommands> executeAsync(Item item)
    {
        // Starts go through the revocation guard; a plain ExecuteAsync would race stop commands that
        // the freed-up control lane can now deliver while this batch is queued or running.
        if (item.Command is StartAgents starts)
        {
            var successful = await StartAgents.StartBatchAsync(_runtime, starts.AgentUris,
                uri => _controller.StartAgentGuardedAsync(uri, item.Sequence), _cancellation);

            return [new AgentsStarted(successful)];
        }

        return await item.Command.ExecuteAsync(_runtime, _cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();

        try
        {
            var worker = _worker;
            await worker;
        }
        catch (Exception)
        {
            // Shutting down; a worker that faulted on the way out is not interesting.
        }
    }
}
