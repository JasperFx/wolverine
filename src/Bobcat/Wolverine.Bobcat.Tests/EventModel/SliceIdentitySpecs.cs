using Bobcat;
using Bobcat.Xunit;

namespace Wolverine.Bobcat.Tests.EventModel;

/// <summary>
/// The projected twin of <c>SliceIdentity.feature</c>, moved here from Bobcat's acceptance suite with
/// <see cref="SpecIdentityAudit"/> (GH-5082): its slices are what <see cref="SpecIdentityAuditEndToEndTests"/>
/// audits out of this assembly's real generated <c>BobcatEventModelSource</c>. Deliberately trivial: the subject under test is
/// the identities the generator emits, not the arithmetic.
/// </summary>
/// <remarks>
/// The Gherkin lane's step-less scenario is a pending claim; here it is
/// <c>[BobcatSpec(Pending = true)]</c>, which is skipped by design and still a claim on its slice.
/// </remarks>
[BobcatFeature("Slice Identity")]
[BobcatSlice(SliceName = "CreditWallet", Domain = "Acceptance", Chapter = "Identity")]
public class SliceIdentitySpecs
{
    private int _balance;
    private bool _refused;

    private void credit(int amount)
    {
        if (amount <= 0)
        {
            _refused = true;
            return;
        }

        _balance += amount;
    }

    [Fact]
    public void A_credited_wallet_shows_the_new_balance()
    {
        // Given a wallet with 100
        _balance = 100;

        // When 25 is credited
        credit(25);

        // Then the balance should be 125
        _balance.ShouldBe(125);
    }

    [Fact]
    public void Crediting_nothing_is_refused()
    {
        // Given a wallet with 100
        _balance = 100;

        // When 0 is credited
        credit(0);

        // Then the credit is refused
        _refused.ShouldBeTrue();
    }

    [BobcatSpec(Pending = true)]
    [BobcatSlice(SliceName = "WalletBalance", Domain = "Acceptance", Chapter = "Identity")]
    public void The_balance_view_is_not_specified_yet()
    {
    }
}
