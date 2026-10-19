using Bobcat.Xunit;

namespace BankAccountES.Tests;

/// <summary>
/// The Account read model is a View slice -- event, projection, document -- and the store derives one per
/// registered projection, named after the DOCUMENT type (jasperfx#825, registered by AddMarten / AddFisher).
/// These specs bind the same slice by the same name, <c>[BobcatSpec(typeof(Account))]</c>, so the spec rung
/// and the store rung fold into ONE sticky. Nothing enforces that the two agree on the name;
/// <see cref="EventModel.the_store_rung_and_the_spec_rung_describe_one_account_slice_not_two" /> is what
/// keeps it true (bobcat#300).
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no act: a View slice receives no command. Its arranged events are the events the
/// projection folds, which is why the generator stamps them as what the slice CONSUMES (bobcat#297).
/// </para>
/// <para>
/// The store says the Account projection applies FOUR events, and lists merge by equality, not union: a
/// spec-declared list that names fewer is a SourceDisagreement hotspot. So between them these two specs
/// arrange all four. Drop one and the Event Model's "no source disagreement" goes red, which is the right
/// reading: the view is under-specified.
/// </para>
/// </remarks>
[BobcatFeature("Account View")]
public class AccountView(BankAccountHost app) : BankAccountSpec(app)
{
    [BobcatSpec(typeof(Account), Domain = "Banking", Pattern = "View")]
    public async Task the_account_read_model_folds_the_streams_history()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId,
            new AccountOpened(accountId, Guid.NewGuid(), "EUR"),
            new FundsDeposited(accountId, 250, 250));

        await ThenReadModel<Account>(accountId,
            Specify<Account>().With(x => x.Balance, 250m).With(x => x.Currency, "EUR").With(x => x.IsFrozen, false));
    }

    [BobcatSpec(typeof(Account), Domain = "Banking", Pattern = "View")]
    public async Task the_account_read_model_folds_a_withdrawal_and_a_freeze()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId,
            new AccountOpened(accountId, Guid.NewGuid(), "USD"),
            new FundsDeposited(accountId, 100, 100),
            new FundsWithdrawn(accountId, 40, 60),
            new AccountFrozen(accountId, "Suspected fraud"));

        await ThenReadModel<Account>(accountId, Specify<Account>().With(x => x.Balance, 60m).With(x => x.IsFrozen, true));
    }
}
