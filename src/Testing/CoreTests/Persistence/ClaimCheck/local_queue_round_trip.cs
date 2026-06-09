using System.Text;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// Local-queue counterpart to <see cref="end_to_end_round_trip"/>. The cross-transport
/// suite only ever exercises the TCP path, which forces a serialize -> deserialize round
/// trip on the receiver. A message whose handler lives in the same process routes to an
/// in-process local queue instead, and a *durable* local queue serializes the envelope on
/// store (DurableLocalQueue.writeMessageData -> _serializer.Write) which fires the
/// claim-check off-load + Clear side-effect against the live in-memory message. This test
/// pins two things at once:
///   1. the handler sees the fully re-hydrated payload (the local-queue re-hydration bug), and
///   2. the off-loaded payload is NOT present in the serialized envelope body — i.e. the fix
///      that restores the live message after serialization does NOT smuggle the blob back into
///      the bytes on the bus and quietly defeat claim-check.
/// </summary>
public class local_queue_round_trip : IAsyncLifetime
{
    private string _claimCheckDirectory = null!;

    public Task InitializeAsync()
    {
        CapturedMessages.Reset();
        _claimCheckDirectory = Path.Combine(Path.GetTempPath(),
            "wolverine-claim-check-local-tests-" + Guid.NewGuid().ToString("N"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_claimCheckDirectory))
            {
                Directory.Delete(_claimCheckDirectory, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup failures
        }

        return Task.CompletedTask;
    }

    private async Task<IHost> StartHostAsync()
    {
        return await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseClaimCheck(c => c.UseFileSystem(_claimCheckDirectory));

            // Durable local queues serialize the envelope on store, which is the path that
            // triggers the claim-check off-load + Clear side-effect on the in-memory message.
            // A buffered (in-memory) local queue never serializes on the local hand-off, so the
            // off-load never fires there and the bug does not reproduce.
            opts.Policies.UseDurableLocalQueues();
        }).StartAsync();
    }

    [Fact]
    public async Task durable_local_queue_round_trips_a_string_blob_property()
    {
        // Distinctive sentinel so the body-content assertion below is unambiguous, and long
        // enough that there is no inline/off-load threshold ambiguity.
        var marker = "LOCAL-CLAIMCHECK-BODY-" + Guid.NewGuid().ToString("N");
        var body = string.Join("\n", Enumerable.Repeat(marker, 200));

        using var host = await StartHostAsync();

        // SendMessageAndWaitAsync (not InvokeMessageAndWaitAsync) is deliberate: invoke executes
        // the handler inline in the caller's context and never routes through the local queue's
        // store-and-forward path, so it would not exercise the durable Write/Clear side-effect.
        var session = await host.TrackActivity()
            .SendMessageAndWaitAsync(new BlobStringMessage("note", body));

        // 1. The handler must see the re-hydrated body. Before the fix this is null: the durable
        //    queue's Write nulls the property on the same in-memory message that the receiver
        //    re-enqueues, and the receive path skips deserialization because envelope.Message
        //    is not null.
        var received = CapturedMessages.LastOf<BlobStringMessage>();
        received.ShouldNotBeNull();
        received.Title.ShouldBe("note");
        received.Body.ShouldBe(body);

        // 2. Claim-check must still be in force on the bus: the off-loaded payload must be
        //    replaced by a header token and must NOT appear in the serialized envelope body.
        //    This is the guard against the restore-on-send fix accidentally leaking the blob
        //    back into the bytes that get persisted/transmitted.
        var sent = session.Sent.SingleEnvelope<BlobStringMessage>();
        sent.Headers.Keys.ShouldContain(ClaimCheckHeaders.Prefix + nameof(BlobStringMessage.Body));

        var serializedBody = sent.Data;
        serializedBody.ShouldNotBeNull();
        Encoding.UTF8.GetString(serializedBody!).ShouldNotContain(marker);
    }

    [Fact]
    public async Task durable_local_queue_round_trips_a_byte_array_blob_property()
    {
        // A recognizable byte pattern that is vanishingly unlikely to appear in the JSON
        // envelope scaffolding by chance, so "payload absent from body" is a real assertion.
        var payload = Enumerable.Range(0, 2048).Select(i => (byte)(0xA0 | (i & 0x0F))).ToArray();

        using var host = await StartHostAsync();

        var session = await host.TrackActivity()
            .SendMessageAndWaitAsync(new BlobByteArrayMessage("doc.pdf", payload));

        var received = CapturedMessages.LastOf<BlobByteArrayMessage>();
        received.ShouldNotBeNull();
        received.Name.ShouldBe("doc.pdf");
        received.Payload.ShouldNotBeNull();
        received.Payload!.ShouldBe(payload);

        var sent = session.Sent.SingleEnvelope<BlobByteArrayMessage>();
        sent.Headers.Keys.ShouldContain(ClaimCheckHeaders.Prefix + nameof(BlobByteArrayMessage.Payload));

        var serializedBody = sent.Data;
        serializedBody.ShouldNotBeNull();
        ContainsSubsequence(serializedBody!, payload).ShouldBeFalse(
            "the off-loaded blob payload must not be present in the serialized envelope body");
    }

    // Naive substring search over bytes: enough to prove the contiguous payload is absent.
    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
