using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-3926. SQS rejects any message over 256KB with a permanent SenderFault, so an endpoint can opt
/// into splitting an oversized body across several SQS messages and putting it back together on the
/// other side.
/// </summary>
public class sqs_message_fragmentation_3926
{
    private static string bodyOfSize(int length)
    {
        return new string('x', length);
    }

    [Fact]
    public void only_a_body_sqs_would_reject_needs_splitting()
    {
        SqsMessageFragments.ExceedsLimit(bodyOfSize(SqsMessageFragments.MaximumBodyBytes)).ShouldBeFalse();
        SqsMessageFragments.ExceedsLimit(bodyOfSize(SqsMessageFragments.MaximumBodyBytes + 1)).ShouldBeTrue();
    }

    [Fact]
    public void a_body_between_the_fragment_size_and_the_sqs_limit_still_goes_as_one_message()
    {
        // Fragments are cut smaller than SQS's cap on purpose, but a body in that band is one SQS will
        // happily take. Deciding on the fragment size instead of the real limit would split -- or worse,
        // discard -- messages that have always sent perfectly well.
        SqsMessageFragments.FragmentBodyBytes.ShouldBeLessThan(SqsMessageFragments.MaximumBodyBytes);

        SqsMessageFragments.ExceedsLimit(bodyOfSize(SqsMessageFragments.FragmentBodyBytes + 1)).ShouldBeFalse();
    }

    [Fact]
    public void the_body_allowance_leaves_room_for_attributes_under_the_hard_limit()
    {
        SqsMessageFragments.MaximumBodyBytes.ShouldBeLessThan(SqsMessageFragments.MaximumMessageBytes);
    }

    [Fact]
    public void splitting_and_concatenating_round_trips_the_body()
    {
        var body = string.Concat(Enumerable.Range(0, 500_000).Select(i => (char)('a' + i % 26)));

        var fragments = SqsMessageFragments.Split(body);

        fragments.Length.ShouldBe(3);
        fragments.ShouldAllBe(x => x.Length <= SqsMessageFragments.FragmentBodyBytes);
        string.Concat(fragments).ShouldBe(body);
    }

    [Fact]
    public void a_body_just_over_the_limit_splits_into_two()
    {
        SqsMessageFragments.Split(bodyOfSize(SqsMessageFragments.MaximumBodyBytes + 1)).Length.ShouldBe(2);
    }

    [Fact]
    public void every_fragment_is_small_enough_for_sqs_to_accept()
    {
        var fragments = SqsMessageFragments.Split(bodyOfSize(SqsMessageFragments.FragmentBodyBytes * 4 + 17));

        fragments.ShouldAllBe(x => x.Length <= SqsMessageFragments.MaximumBodyBytes);
    }

    [Fact]
    public void reads_back_the_header_it_wrote()
    {
        var id = Guid.NewGuid();
        var message = new Message { MessageAttributes = SqsMessageFragments.AttributesFor(id, 2, 5) };

        SqsMessageFragments.TryReadHeader(message, out var header).ShouldBeTrue();

        header.FragmentId.ShouldBe(id);
        header.Index.ShouldBe(2);
        header.Count.ShouldBe(5);
    }

    [Fact]
    public void an_ordinary_message_has_no_fragment_header()
    {
        SqsMessageFragments.TryReadHeader(new Message(), out _).ShouldBeFalse();

        SqsMessageFragments
            .TryReadHeader(new Message { MessageAttributes = new Dictionary<string, MessageAttributeValue>() }, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void a_header_with_nonsense_values_is_rejected_rather_than_trusted()
    {
        // Index outside the set: reading this as a real header would index past the buffer.
        var attributes = SqsMessageFragments.AttributesFor(Guid.NewGuid(), 0, 3);
        attributes[SqsMessageFragments.FragmentIndexAttribute].StringValue = "7";

        SqsMessageFragments.TryReadHeader(new Message { MessageAttributes = attributes }, out _).ShouldBeFalse();
    }

    [Fact]
    public void the_envelopes_own_group_id_wins_over_the_fragment_id()
    {
        // The caller already said which partition this message belongs to; overriding that would send
        // the fragments somewhere other than where the whole message was meant to go.
        var envelope = new Envelope { GroupId = "tenant-3" };

        SqsMessageFragments.GroupIdFor(envelope, Guid.NewGuid()).ShouldBe("tenant-3");
    }

    [Fact]
    public void the_fragment_id_stands_in_when_there_is_no_group_id()
    {
        var id = Guid.NewGuid();

        var groupId = SqsMessageFragments.GroupIdFor(new Envelope(), id);

        groupId.ShouldContain(id.ToString());
        groupId.ShouldNotBeEmpty();
    }

    [Fact]
    public void every_fragment_of_one_message_shares_a_group_id()
    {
        var envelope = new Envelope();
        var id = Guid.NewGuid();

        Enumerable.Range(0, 4).Select(_ => SqsMessageFragments.GroupIdFor(envelope, id)).Distinct().Count().ShouldBe(1);
    }
}

/// <summary>
/// GH-3926. The receiving half: holding partial fragment sets in memory until they complete.
/// </summary>
public class sqs_fragment_reassembler_3926
{
    private readonly SqsFragmentReassembler _reassembler = new(5.Minutes(), NullLogger.Instance);
    private readonly Guid _id = Guid.NewGuid();

    private Message fragment(int index, int count, string body)
    {
        return new Message
        {
            Body = body,
            ReceiptHandle = $"{_id}-{index}",
            MessageAttributes = SqsMessageFragments.AttributesFor(_id, index, count)
        };
    }

    private bool accept(Message message, out string body, out Message[] messages)
    {
        SqsMessageFragments.TryReadHeader(message, out var header).ShouldBeTrue();
        return _reassembler.TryAccept(message, header, out body, out messages);
    }

    [Fact]
    public void holds_an_incomplete_set_and_completes_on_the_last_fragment()
    {
        accept(fragment(0, 3, "one "), out _, out _).ShouldBeFalse();
        _reassembler.PartialCount.ShouldBe(1);

        accept(fragment(1, 3, "two "), out _, out _).ShouldBeFalse();
        _reassembler.PartialCount.ShouldBe(1);

        accept(fragment(2, 3, "three"), out var body, out var messages).ShouldBeTrue();

        body.ShouldBe("one two three");
        messages.Length.ShouldBe(3);

        // The completed set is handed over, not retained
        _reassembler.PartialCount.ShouldBe(0);
    }

    [Fact]
    public void reassembles_in_index_order_regardless_of_arrival_order()
    {
        // SQS makes no ordering promise on a standard queue, so the index in the header is the only
        // thing that says where a fragment belongs.
        accept(fragment(2, 3, "three"), out _, out _).ShouldBeFalse();
        accept(fragment(0, 3, "one "), out _, out _).ShouldBeFalse();
        accept(fragment(1, 3, "two "), out var body, out _).ShouldBeTrue();

        body.ShouldBe("one two three");
    }

    [Fact]
    public void a_redelivered_fragment_is_not_an_error()
    {
        // Standard queues are at-least-once, so the same fragment arriving twice is expected.
        accept(fragment(0, 2, "one "), out _, out _).ShouldBeFalse();
        accept(fragment(0, 2, "one "), out _, out _).ShouldBeFalse();
        _reassembler.PartialCount.ShouldBe(1);

        accept(fragment(1, 2, "two"), out var body, out var messages).ShouldBeTrue();

        body.ShouldBe("one two");
        messages.Length.ShouldBe(2);
    }

    [Fact]
    public void hands_back_every_message_in_the_set_so_completion_can_delete_them_all()
    {
        accept(fragment(0, 2, "a"), out _, out _);
        accept(fragment(1, 2, "b"), out _, out var messages).ShouldBeTrue();

        messages.Select(x => x.ReceiptHandle).ShouldBe([$"{_id}-0", $"{_id}-1"]);
    }

    [Fact]
    public void two_messages_being_reassembled_at_once_do_not_bleed_into_each_other()
    {
        accept(fragment(0, 2, "mine-"), out _, out _).ShouldBeFalse();

        // A different fragment id is a separate set, and completing it must not disturb the first
        var stranger = new Message
        {
            Body = "theirs",
            MessageAttributes = SqsMessageFragments.AttributesFor(Guid.NewGuid(), 0, 1)
        };

        SqsMessageFragments.TryReadHeader(stranger, out var header).ShouldBeTrue();
        _reassembler.TryAccept(stranger, header, out var body, out _).ShouldBeTrue();
        body.ShouldBe("theirs");

        _reassembler.PartialCount.ShouldBe(1);

        accept(fragment(1, 2, "second"), out var mine, out _).ShouldBeTrue();
        mine.ShouldBe("mine-second");
    }

    [Fact]
    public void abandons_a_set_that_never_completes_within_the_timeout()
    {
        // Nothing is deleted from SQS while a set is incomplete, so abandoning it here just means the
        // fragments become visible again rather than the buffer growing forever. A negative timeout puts
        // the cutoff in the future, so the sweep is decided by the code under test rather than by how
        // fast the test ran.
        var reassembler = new SqsFragmentReassembler(-1.Minutes(), NullLogger.Instance);

        SqsMessageFragments.TryReadHeader(fragment(0, 3, "one"), out var first).ShouldBeTrue();
        reassembler.TryAccept(fragment(0, 3, "one"), first, out _, out _).ShouldBeFalse();

        // A later arrival sweeps the expired set before recording itself
        var later = new Message
        {
            Body = "x",
            MessageAttributes = SqsMessageFragments.AttributesFor(Guid.NewGuid(), 0, 2)
        };

        SqsMessageFragments.TryReadHeader(later, out var header).ShouldBeTrue();
        reassembler.TryAccept(later, header, out _, out _).ShouldBeFalse();

        reassembler.PartialCount.ShouldBe(1);
    }
}
