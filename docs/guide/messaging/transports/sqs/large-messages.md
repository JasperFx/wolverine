# Large Messages

Amazon SQS caps a message at **256KB** — body and message attributes together. Go over it and SQS answers:

```
InvalidParameterValue - Message must be shorter than 262144 bytes. (SenderFault: true)
```

`SenderFault: true` means SQS is telling you the request itself is wrong, so the identical request will
fail identically forever. Wolverine treats an oversized message accordingly: it is **logged and
discarded** rather than retried, so a message that outgrew the limit produces one error rather than an
endless flood of them.

If you want the message to actually get through, there are two answers.

## Claim Checks (recommended)

A [claim check](/guide/durability/claim-checks) writes the payload to S3 and sends a small pointer through
SQS, which is the pattern AWS itself recommends. It has no practical size ceiling, it works on standard
queues with any number of competing consumers, and what travels through SQS stays an ordinary message
that anything can consume.

```bash
dotnet add package WolverineFx.ClaimCheck.AmazonS3
```

```csharp
opts.UseClaimCheck(claimCheck =>
{
    claimCheck.UseAmazonS3FromServices(bucketName: "wolverine-claim-checks");

    // Anything whose serialized body is larger than 200KB is off-loaded whole
    claimCheck.AutoOffloadPayloadsLargerThan(200 * 1024);
});
```

**This is the right answer for almost everyone.** Reach for fragmentation below only when adding an S3
bucket is genuinely not an option.

## Message Fragmentation <Badge type="tip" text="6.27" />

Fragmentation splits an oversized body across several SQS messages and reassembles it on the receiving
side, with no extra infrastructure at all. It is opt-in per endpoint:

```csharp
opts.PublishMessage<BigDocumentReceived>()
    .ToSqsQueue("documents")
    .FragmentOversizedMessages();

opts.ListenToSqsQueue("documents")
    .FragmentOversizedMessages();
```

Nothing changes for messages that fit; only a body over the limit is split.

### The constraint you have to design around

**Reassembly happens in memory, on one listener.** SQS is a competing-consumer queue, so if several nodes
poll the same standard queue, fragments of a single message can be handed to different nodes and no one of
them ever holds the whole set. Those partial sets are abandoned after the reassembly timeout (5 minutes by
default) and redelivered — the message may eventually get through, but only by luck, and meanwhile the
queue churns.

So only use fragmentation in one of these three shapes:

| Topology | Why it is safe |
|---|---|
| A **FIFO queue** | SQS delivers a message group to one consumer at a time, and every fragment of a message shares a group id. |
| A **globally partitioned** listener | The fragments carry the message's `GroupId`, so the whole message routes to the node that owns that group. |
| A **single listening node** | There is nobody to compete with. |

[Global partitioning](/guide/messaging/partitioning#global-partitioning) is the recommended shape — it
keeps multiple nodes and still makes reassembly reliable rather than best-effort:

```csharp
opts.MessagePartitioning.ByMessage<IDocumentMessage>(x => x.GroupId);

opts.MessagePartitioning.GlobalPartitioned(topology =>
{
    topology.UseShardedAmazonSqsQueues("documents", 4, sqs =>
    {
        sqs.ConfigureSender(x => x.FragmentOversizedMessages());
        sqs.ConfigureListening(x => x.FragmentOversizedMessages());
    });

    topology.MessagesImplementing<IDocumentMessage>();
});
```

### Other things worth knowing

* **Wolverine to Wolverine only.** The fragments are Wolverine's own framing, carried in SQS message
  attributes. A non-Wolverine consumer reading the queue sees N unintelligible messages rather than one.
  That is exactly why this is opt-in rather than automatic.

* **Configure both ends.** A listener reassembles fragments whether or not it was configured to send that
  way — the framing is self-describing — but the same setting also governs requeues and the reassembly
  timeout, so set it on the sending and listening endpoints alike.

* **Nothing is acknowledged until the set is complete.** Fragments are never deleted from SQS until every
  one of them is in hand, so a node that crashes holding two of three has told SQS nothing and all three
  become visible again. Completing a reassembled message deletes all of its fragments together.

* **Group ids are respected.** If the envelope already has a `GroupId` — from `DeliveryOptions` or
  [message partitioning](/guide/messaging/partitioning) — every fragment carries it, so the message lands
  where it was always going to land. Only a message with no group id at all gets a synthesized one, purely
  to hold the set together.

* **There is a ceiling.** A message needing more than 10 fragments is discarded with an error pointing at
  claim checks. At that size this is a storage problem, not a framing one.

* **On a FIFO queue** each fragment gets a distinct `MessageDeduplicationId` (the envelope's, suffixed with
  the fragment index), since a shared one would have SQS keep exactly one fragment of the set.

### Tuning the reassembly timeout

```csharp
opts.ListenToSqsQueue("documents")
    .FragmentOversizedMessages(reassemblyTimeout: 2.Minutes());
```

A listener holding an incomplete set past this timeout abandons it and logs a warning naming the fragment
id and how many of the set it held. Abandoning is local only — the fragments were never deleted from SQS,
so they become visible again. Repeated warnings here are the signal that your topology is not one of the
three above.
