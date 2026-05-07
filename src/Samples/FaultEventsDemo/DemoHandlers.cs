namespace FaultEventsDemo;

// --- Messages ----------------------------------------------------------------

public sealed record OrderPlaced(Guid Id, string Sku);

public sealed record PaymentDetails(string CardNumber, decimal Amount);

public sealed record HighVolumeChatter(int Tick);

// --- Handlers ----------------------------------------------------------------

// Always throws — the OnException<InvalidOperationException>().MoveToErrorQueue()
// rule in Program.cs makes this terminal on the first attempt, so the demo
// does not have to wait for retry exhaustion. The DLQ move triggers the
// auto-publish of Fault<OrderPlaced>.
public static class OrderPlacedHandler
{
    public static void Handle(OrderPlaced msg) =>
        throw new InvalidOperationException($"could not place order {msg.Id} for {msg.Sku}");
}

// Succeeds. Included to demonstrate that the per-type Encrypt() pairing in
// Program.cs is configured even when the handler does not fail — the
// PaymentDetails envelope and any Fault<PaymentDetails> use the encrypting
// serializer.
public static class PaymentDetailsHandler
{
    public static void Handle(PaymentDetails msg) =>
        Console.WriteLine($"Payment received: {msg.Amount} on card ending {msg.CardNumber[^4..]}");
}

// Always throws but is opted out of fault publishing in Program.cs via
// DoNotPublishFault(). Verifies that no Fault<HighVolumeChatter> is auto-
// published — the failure goes to the error queue silently.
public static class HighVolumeChatterHandler
{
    public static void Handle(HighVolumeChatter msg) =>
        throw new InvalidOperationException($"chatter tick {msg.Tick} is noise");
}

// --- Fault subscriber --------------------------------------------------------

// Subscribes to Fault<OrderPlaced>. Reads FaultHeaders.AutoPublished from
// the envelope to demonstrate the auto vs. manual distinction (only auto-
// published faults carry "wolverine.fault.auto" = "true").
public static class OrderPlacedFaultHandler
{
    public static void Handle(Wolverine.Fault<OrderPlaced> fault, Wolverine.Envelope envelope)
    {
        var auto = envelope.Headers.TryGetValue(Wolverine.FaultHeaders.AutoPublished, out var v)
            && v == "true";
        Console.WriteLine(
            $"Fault captured for order {fault.Message.Id} ({(auto ? "auto" : "manual")}): " +
            $"{fault.Exception.Type} — {fault.Exception.Message} (attempt {fault.Attempts})");
    }
}
