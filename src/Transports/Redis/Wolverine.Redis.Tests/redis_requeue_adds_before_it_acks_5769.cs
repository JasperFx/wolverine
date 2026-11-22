using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
///     GH-5769. A requeue (<c>DeferAsync</c>, and <c>TryRequeueAsync</c> on top of it) used to settle the pending
///     entry first -- <c>XACK</c>, or <c>XACKDEL</c> with <c>DeleteStreamEntryOnAck</c> -- and only then
///     <c>XADD</c> the copy, all inside a catch-all that logged. A failed <c>XADD</c> left nothing pending for
///     <c>XAUTOCLAIM</c> to recover and nothing reached the caller's retry: the message was gone.
///
///     <para>Now the copy is added first and the original is acknowledged only once it is on the stream, and a
///     failure reaches the caller. Deterministic against a substituted <see cref="IDatabase" />; the broker
///     behaviour of the requeue itself is covered by the compliance and retry suites.</para>
/// </summary>
public class redis_requeue_adds_before_it_acks_5769
{
    private const string StreamKey = "requeue-5769";
    private const string Group = "requeue-5769-group";
    private const string EntryId = "1700000000000-0";

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RedisTransport _transport;
    private readonly RedisStreamListener _listener;
    private readonly RedisStreamEndpoint _endpoint;

    public redis_requeue_adds_before_it_acks_5769()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_database);

        _transport = new RedisTransport(connection);
        _endpoint = _transport.StreamEndpoint(StreamKey);
        _endpoint.ConsumerGroup = Group;
        _endpoint.EnvelopeMapper = new RedisEnvelopeMapper(_endpoint);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Options.Returns(new WolverineOptions { ServiceName = "Requeue5769" });

        _listener = new RedisStreamListener(_transport, _endpoint, runtime, Substitute.For<IReceiver>(), connection);
    }

    /// <summary>
    ///     The envelope exactly as the listener builds it from a stream entry it read
    /// </summary>
    private Envelope receivedEnvelope()
    {
        var original = new Envelope
        {
            Id = Guid.NewGuid(),
            Data = Encoding.UTF8.GetBytes("{}"),
            ContentType = "application/json",
            MessageType = "requeue-5769"
        };

        var fields = new List<NameValueEntry>();
        _endpoint.EnvelopeMapper!.MapEnvelopeToOutgoing(original, fields);

        return _endpoint.EnvelopeMapper.CreateEnvelope(StreamKey, new StreamEntry(EntryId, fields.ToArray()), null);
    }

    private void addFails() =>
        _database.StreamAddAsync(default, default(NameValueEntry[])!)
            .ThrowsAsyncForAnyArgs(new RedisConnectionException(ConnectionFailureType.SocketFailure, "XADD failed"));

    private void ackFailsOnce()
    {
        var calls = 0;
        _database.StreamAcknowledgeAsync(default, default, default(RedisValue))
            .ReturnsForAnyArgs(_ => ++calls == 1
                ? Task.FromException<long>(new RedisConnectionException(ConnectionFailureType.SocketFailure, "XACK failed"))
                : Task.FromResult(1L));
    }

    private string[] redisCalls() => _database.ReceivedCalls()
        .Select(x => x.GetMethodInfo().Name)
        .Where(x => x.StartsWith("Stream"))
        .ToArray();

    [Fact]
    public async Task the_copy_is_added_before_the_original_is_acknowledged()
    {
        await _listener.DeferAsync(receivedEnvelope());

        redisCalls().ShouldBe(["StreamAddAsync", "StreamAcknowledgeAsync"]);
    }

    [Fact]
    public async Task with_delete_on_ack_the_copy_is_added_before_the_original_is_deleted()
    {
        _transport.DeleteStreamEntryOnAck = true;

        await _listener.DeferAsync(receivedEnvelope());

        redisCalls().ShouldBe(["StreamAddAsync", "StreamAcknowledgeAndDeleteAsync"]);
    }

    [Fact]
    public async Task a_failed_add_leaves_the_original_pending_and_reaches_the_caller()
    {
        addFails();

        // Reaching the caller is what lets the receivers' retry blocks retry the requeue
        await Should.ThrowAsync<RedisConnectionException>(async () => await _listener.DeferAsync(receivedEnvelope()));

        // Still pending, so XAUTOCLAIM can recover it if every retry fails
        redisCalls().ShouldNotContain("StreamAcknowledgeAsync");
    }

    [Fact]
    public async Task a_failed_add_is_not_a_native_requeue()
    {
        addFails();

        // false: BufferedReceiver keeps the message and runs it again itself, while the entry stays pending
        (await _listener.TryRequeueAsync(receivedEnvelope())).ShouldBeFalse();

        redisCalls().ShouldNotContain("StreamAcknowledgeAsync");
    }

    [Fact]
    public async Task a_retry_after_a_failed_ack_does_not_add_a_second_copy()
    {
        ackFailsOnce();
        var envelope = receivedEnvelope();

        await Should.ThrowAsync<RedisConnectionException>(async () => await _listener.DeferAsync(envelope));

        // What the retry block does next
        await _listener.DeferAsync(envelope);

        redisCalls().ShouldBe(["StreamAddAsync", "StreamAcknowledgeAsync", "StreamAcknowledgeAsync"]);
    }

    [Fact]
    public async Task a_requeue_whose_copy_was_added_but_whose_ack_failed_is_still_a_native_requeue()
    {
        ackFailsOnce();

        // The copy is on the stream. Handing the message back to the caller as well would run it twice; the
        // still-pending original costs at most a duplicate through XAUTOCLAIM.
        (await _listener.TryRequeueAsync(receivedEnvelope())).ShouldBeTrue();

        redisCalls().Count(x => x == "StreamAddAsync").ShouldBe(1);
    }
}
