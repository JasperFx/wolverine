using Wolverine;
using Wolverine.Polecat;

namespace EventSourcedSagas.GiftCards;

// GH-5415: the GiftCard saga again, kept in an ancillary Polecat store, named with Polecat's own [PolecatStore]

public interface IPolecatGiftCardStore : Polecat.IDocumentStore;

public record IssuePolecatGiftCard(Guid PolecatGiftCardId, decimal Amount);

public record RedeemPolecatGiftCard(Guid PolecatGiftCardId, decimal Amount);

[PolecatStore(typeof(IPolecatGiftCardStore))]
public class PolecatGiftCard : EventSourcedSaga
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static GiftCardIssued Start(IssuePolecatGiftCard issue) => new(issue.Amount);

    public IEnumerable<object> Handle(RedeemPolecatGiftCard redeem)
    {
        yield return new GiftCardRedeemed(redeem.Amount);
        if (redeem.Amount >= Balance) yield return new SagaCompleted();
    }

    public void Apply(GiftCardIssued e) => Balance = e.Amount;

    public void Apply(GiftCardRedeemed e) => Balance -= e.Amount;
}
