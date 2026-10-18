namespace BankAccountES.Tests;

/// <summary>
/// The command slices, driven over HTTP the way a client would: each spec arranges the aggregate's
/// history as events, sends ONE request, and asserts what it appended and what the aggregate became.
/// </summary>
/// <remarks>
/// <para>
/// <b>The slice binding.</b> <c>[BobcatSpec(typeof(X))]</c> names the slice after the request type, which
/// is the same key the Wolverine.Http chain derives, so these specs land on the same slices as the derived
/// roles. The roles themselves -- command, aggregate, emitted events -- come off the vocabulary each spec
/// calls (bobcat#462). <c>Domain</c> is restated per spec because a method's binding replaces the class's.
/// </para>
/// <para>
/// <b>The aggregate is read back, not echoed.</b> <c>ThenAggregate</c> rebuilds the aggregate from its
/// stream; asserting against what the POST answered would test nothing.
/// </para>
/// </remarks>
[BobcatFeature("Bank Account Event Sourcing")]
public class BankAccountEventSourcing(BankAccountHost app) : BankAccountSpec(app)
{
    [BobcatSpec(typeof(EnrollClient), Domain = "Banking")]
    public async Task enroll_a_client()
    {
        await WhenPosted(new EnrollClient("John Doe", "john@example.com"), "/api/clients");

        ThenTheResponseStatusIs(201);
        ThenEvents(Specify<ClientEnrolled>().With(x => x.Name, "John Doe").With(x => x.Email, "john@example.com"));
        await ThenAggregate<Client>(TheStartedStream<Client>(),
            Specify<Client>().With(x => x.Name, "John Doe").With(x => x.Email, "john@example.com"));
    }

    [BobcatSpec(typeof(UpdateClient), Domain = "Banking")]
    public async Task update_a_client()
    {
        var clientId = Guid.NewGuid();
        await GivenEvents<Client>(clientId, new ClientEnrolled(clientId, "Jane Doe", "jane@example.com"));

        // The command carries the email as well as the name, so the update replays the enrolled address
        // By route name rather than URL (bobcat#500): the slice still reaches the model as PUT /api/clients/{clientId}
        await WhenPutTo(new UpdateClient(clientId, "Jane Smith", "jane@example.com"), "UpdateClient", clientId);

        ThenTheResponseStatusIs(204);
        ThenEvents(new ClientUpdated(clientId, "Jane Smith", "jane@example.com"));
        await ThenAggregate<Client>(clientId,
            Specify<Client>().With(x => x.Name, "Jane Smith").With(x => x.Email, "jane@example.com"));
    }

    [BobcatSpec(typeof(OpenAccount), Domain = "Banking")]
    public async Task open_a_bank_account()
    {
        var clientId = Guid.NewGuid();
        // A lookup, not the stream the command decides against: the endpoint's [Entity] loads the client
        await GivenContext<Client>(clientId, new ClientEnrolled(clientId, "Alice", "alice@example.com"));

        await WhenPosted(new OpenAccount(clientId), "/api/accounts");

        ThenTheResponseStatusIs(201);
        ThenEvents(Specify<AccountOpened>().With(x => x.ClientId, clientId).With(x => x.Currency, "USD"));
        await ThenAggregate<Account>(TheStartedStream<Account>(), Specify<Account>().With(x => x.Balance, 0m));
    }

    [BobcatSpec(typeof(OpenAccount), Domain = "Banking")]
    public async Task open_an_account_for_a_client_who_was_never_enrolled()
    {
        // [Entity(Required = true)] answers 400 before the endpoint runs at all
        await WhenPosted(new OpenAccount(Guid.Parse("11111111-1111-1111-1111-111111111111")), "/api/accounts");

        ThenTheResponseStatusIs(400);
        ThenNoEvents();
    }

    [BobcatSpec(typeof(DepositFunds), Domain = "Banking")]
    public async Task deposit_funds()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId, new AccountOpened(accountId, Guid.NewGuid(), "USD"));

        await WhenPosted(new DepositFunds(accountId, 100), "/api/accounts/{accountId}/deposits", accountId);

        ThenTheResponseStatusIs(204);
        ThenEvents(new FundsDeposited(accountId, 100, 100));
        await ThenAggregate<Account>(accountId, Specify<Account>().With(x => x.Balance, 100m));
    }

    [BobcatSpec(typeof(WithdrawFunds), Domain = "Banking")]
    public async Task withdraw_funds()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId,
            new AccountOpened(accountId, Guid.NewGuid(), "USD"),
            new FundsDeposited(accountId, 200, 200));

        await WhenPosted(new WithdrawFunds(accountId, 50), "/api/accounts/{accountId}/withdrawals", accountId);

        ThenTheResponseStatusIs(204);
        ThenEvents(new FundsWithdrawn(accountId, 50, 150));
        await ThenAggregate<Account>(accountId, Specify<Account>().With(x => x.Balance, 150m));
    }

    // #region overdraft-http
    [BobcatSpec(typeof(WithdrawFunds), Domain = "Banking")]
    public async Task withdrawing_more_than_the_balance_leaves_the_account_untouched()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId,
            new AccountOpened(accountId, Guid.NewGuid(), "USD"),
            new FundsDeposited(accountId, 100, 100));

        await WhenPosted(new WithdrawFunds(accountId, 200), "/api/accounts/{accountId}/withdrawals", accountId);

        ThenTheResponseStatusIs(400);
        ThenNoEvents();
        await ThenAggregate<Account>(accountId, Specify<Account>().With(x => x.Balance, 100m));
    }
    // #endregion overdraft-http

    /// <summary>
    /// A query slice (bobcat#501): the read goes through the GET endpoint, so the spec lands on the slice Wolverine
    /// derives from it, named for its route. It READS <see cref="AccountTransactions" /> -- the store's View slice of
    /// that name produces it, and the two are joined by a <c>ReadModelRead</c> link. The arranged Account stream is
    /// setup feeding that projection, so it stamps nothing on the query.
    /// </summary>
    [BobcatSpec(SliceName = "GET /api/accounts/{accountId}/transactions", Domain = "Banking")]
    public async Task transaction_history_records_every_deposit_and_withdrawal()
    {
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId,
            new AccountOpened(accountId, Guid.NewGuid(), "USD"),
            new FundsDeposited(accountId, 100, 100),
            new FundsWithdrawn(accountId, 30, 70));

        await WhenReading<AccountTransactions>("/api/accounts/{accountId}/transactions", accountId);

        var history = await TheResponse<AccountTransactions>();
        ThereAreTransactions(history, 2);
        TheTransactionHistoryBalanceIs(history, 70);
    }

    /// <summary>
    /// A query slice: Wolverine derives it from the GET endpoint and names it for its route, because a GET
    /// has no request type to key on. The arranged <c>AccountOpened</c> is setup (bobcat#501), so the slice
    /// claims neither to consume it nor to read its stream.
    /// </summary>
    [BobcatSpec(SliceName = "GET /api/clients/{clientId}/accounts", Domain = "Banking")]
    public async Task get_client_accounts()
    {
        var clientId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await GivenEvents<Account>(accountId, new AccountOpened(accountId, clientId, "USD"));

        await WhenReading<Account>("/api/clients/{clientId}/accounts", clientId);

        ThereAreAccounts(await TheResponse<List<Account>>(), 1);
    }

    [Then("there are {count} transactions")]
    public void ThereAreTransactions(AccountTransactions history, int count)
        => SpecAssert.Check("count", history.Transactions.Count, count);

    [Then("the transaction history balance is {balance}")]
    public void TheTransactionHistoryBalanceIs(AccountTransactions history, decimal balance)
        => SpecAssert.Check("balance", history.Balance, balance);

    [Then("there is {count} account")]
    public void ThereAreAccounts(List<Account> accounts, int count)
        => SpecAssert.Check("count", accounts.Count, count);
}
