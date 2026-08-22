using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// GH-3509: the two places the pipeline used to orphan a payload with no token on the wire that could
/// ever reference it. <see cref="claim_check_serializer_restore"/> covers restoring the in-memory
/// message on the same failure path; this covers cleaning up the storage backend.
/// </summary>
public class offload_failure_cleanup
{
    private static ClaimCheckMessageSerializer sutFor(IClaimCheckStore store)
    {
        var inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        return new ClaimCheckMessageSerializer(inner, store);
    }

    [Fact]
    public void a_partial_offload_deletes_the_payloads_it_already_uploaded()
    {
        // The first [Blob] property uploads fine, the second throws. The first payload's token never
        // reaches the wire, so nothing will ever load or delete it -- the serializer must clean it up.
        var store = new FailAfterNClaimCheckStore { FailAfter = 1 };
        var sut = sutFor(store);

        var message = new MultiBlobMessage("multi", new byte[] { 1, 2, 3, 4 }, new string('z', 64));

        Should.Throw<InvalidOperationException>(() => sut.Write(new Envelope(message)));

        store.DeleteCount.ShouldBe(1);
        store.Count.ShouldBe(0);
    }

    [Fact]
    public async Task a_partial_offload_deletes_uploaded_payloads_on_the_async_path_too()
    {
        var store = new FailAfterNClaimCheckStore { FailAfter = 1 };
        var sut = sutFor(store);

        var message = new MultiBlobMessage("multi", new byte[] { 1, 2, 3, 4 }, new string('z', 64));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sut.WriteAsync(new Envelope(message)));

        store.DeleteCount.ShouldBe(1);
        store.Count.ShouldBe(0);
    }

    [Fact]
    public void a_failed_whole_body_offload_deletes_the_property_payloads()
    {
        // Both [Blob] properties upload, then the GH-3504 whole-body off-load throws. The property
        // tokens were stamped onto an envelope that is now never going to be sent.
        var store = new FailAfterNClaimCheckStore { FailAfter = 2 };
        var sut = new ClaimCheckMessageSerializer(
            new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions()),
            store,
            autoOffloadThreshold: 1);

        var message = new MultiBlobMessage("multi", new byte[] { 1, 2, 3, 4 }, new string('z', 64));

        Should.Throw<InvalidOperationException>(() => sut.Write(new Envelope(message)));

        store.DeleteCount.ShouldBe(2);
        store.Count.ShouldBe(0);
    }

    [Fact]
    public void write_message_without_an_envelope_uploads_nothing()
    {
        // There is no envelope here, so a token could never be stamped anywhere -- uploading would
        // orphan a payload on every single call. The properties are still cleared so the inner
        // serializer keeps the body small, then restored on the live message.
        var store = new RecordingInMemoryClaimCheckStore();
        var sut = sutFor(store);

        var image = new byte[] { 1, 2, 3, 4 };
        var notes = new string('z', 64);
        var message = new MultiBlobMessage("multi", image, notes);

        var json = System.Text.Encoding.UTF8.GetString(sut.WriteMessage(message));

        store.StoreCount.ShouldBe(0);

        // The payloads must still be absent from the serialized body...
        json.ShouldNotContain(notes);

        // ...while the live message is left intact for in-process hand-off.
        message.Image.ShouldBe(image);
        message.Notes.ShouldBe(notes);
    }
}
