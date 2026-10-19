using Bobcat.Xunit;

namespace BankAccountES.Tests;

/// <summary>
/// The one message-handled slice: the fraud desk freezes an account over the Wolverine bus.
/// </summary>
/// <remarks>
/// ⚠️ bobcat#172: this spec deliberately does NOT mention the AccountFlagged event the handler also emits.
/// The Wolverine-derived claim on the slice's emitted events therefore disagrees with this Declared one, and
/// the merge must surface that as a SourceDisagreement hotspot rather than swallow it. Do not "fix" this
/// spec by adding the missing event -- the gap is the point, and <see cref="EventModel" /> asserts on it.
/// <c>ThenEmitted&lt;T&gt;</c> is a contains-check, so the spec passes while naming only AccountFrozen.
/// </remarks>
// #region freeze-account
[BobcatFeature("Freeze Account")]
public class FreezeAccountSpecs(BankAccountHost app) : BankAccountSpec(app)
{
    [BobcatSpec(typeof(FreezeAccount), Domain = "Banking", TriggeredBy = "The fraud desk")]
    public async Task freezing_an_account_records_the_freeze()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId, new AccountOpened(accountId, Guid.NewGuid(), "USD"));

        await WhenReceived(new FreezeAccount(accountId, "Suspected fraud"));

        ThenEmitted<AccountFrozen>();
        await ThenReadModel<Account>(accountId, Specify<Account>().With(x => x.IsFrozen, true));
    }
}
// #endregion freeze-account
