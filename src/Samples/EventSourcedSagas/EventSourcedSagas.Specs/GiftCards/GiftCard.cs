using Marten;
using Wolverine;
using Wolverine.Marten;

namespace EventSourcedSagas.GiftCards;

// GH-5415: an event-sourced saga kept in an ancillary Marten store. The saga type names its store, as an aggregate
// handler's would, and Wolverine registers its Inline snapshot in that store.

public interface IGiftCardStore : IDocumentStore;

public record IssueGiftCard(Guid GiftCardId, decimal Amount);

public record RedeemGiftCard(Guid GiftCardId, decimal Amount);

public record GiftCardIssued(decimal Amount);

public record GiftCardRedeemed(decimal Amount);

[MartenStore(typeof(IGiftCardStore))]
public class GiftCard : EventSourcedSaga
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static GiftCardIssued Start(IssueGiftCard issue) => new(issue.Amount);

    public IEnumerable<object> Handle(RedeemGiftCard redeem)
    {
        yield return new GiftCardRedeemed(redeem.Amount);
        if (redeem.Amount >= Balance) yield return new SagaCompleted();
    }

    public void Apply(GiftCardIssued e) => Balance = e.Amount;

    public void Apply(GiftCardRedeemed e) => Balance -= e.Amount;
}
