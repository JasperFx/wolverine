using Customers;
using Payments;
using Users;
using Wallets;

namespace PaymentsMonolith.Tests;

/// <summary>
/// The Inflow conversion: four modules in one process that talk to each other only through durable
/// local queues. Registering a user makes the Customers module create a customer stub; completing that
/// profile makes the Wallets module create a wallet. Neither is a direct call, so these specs are as
/// much about the cascade arriving as about the endpoint returning -- which is why every write is
/// tracked until its cascades are handled.
/// </summary>
/// <remarks>
/// Measured, not assumed: replacing the tracking with a plain call fails 3 of the 11 specs outright.
/// </remarks>
[BobcatFeature("Payments Monolith")]
public class PaymentsSpecs(PaymentsMonolithHost app) : PaymentsMonolithSpec(app)
{
    private const string Password = "Password1!";

    private Guid _customerId;
    private Guid _walletId;
    private Guid _secondCustomerId;
    private Guid _secondWalletId;
    private int _lastStatusCode;
    private Wallet? _wallet;
    private Deposit? _deposit;
    private Customer? _customer;

    [BobcatSpec]
    public async Task registering_a_user_creates_a_customer_profile()
    {
        await WhenAUserRegisters("pay.user@example.com", "Pay User");

        ThenTheResponseStatusIs(200);
        await ThenACustomerProfileExistsForThatUser();
    }

    [BobcatSpec]
    public async Task registering_without_an_email_returns_400()
    {
        await WhenAUserRegisters("", "No Email");

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task registering_an_email_that_is_already_taken_returns_409()
    {
        await GivenARegisteredUser("dup.pay@example.com", "First Claimant");

        await WhenAUserRegisters("dup.pay@example.com", "Second Claimant");

        ThenTheResponseStatusIs(409);
    }

    [BobcatSpec]
    public async Task completing_a_customer_profile_creates_a_wallet()
    {
        await GivenARegisteredUser("complete.user@example.com", "John Doe");

        await WhenTheCustomerProfileIsCompleted("Johnny", "Poland");

        ThenTheResponseStatusIs(200);
        ThenTheCustomerProfileIsMarkedComplete();
        await ThenTheCustomerHasWallets(1);
        ThenTheWalletBalanceIs(0);
    }

    [BobcatSpec]
    public async Task completing_a_customer_that_does_not_exist_returns_404()
    {
        await WhenTheProfileOfAnUnknownCustomerIsCompleted();

        ThenTheResponseStatusIs(404);
    }

    [BobcatSpec]
    public async Task adding_funds_raises_the_wallet_balance()
    {
        await GivenARegisteredUser("addfunds@example.com", "Funds User");
        await GivenACompletedCustomerProfile("Funds", "Poland");

        await WhenFundsAreAdded(100);

        ThenTheResponseStatusIs(200);
        ThenTheWalletBalanceIs(100);
    }

    [BobcatSpec]
    public async Task adding_a_negative_amount_returns_400()
    {
        await GivenARegisteredUser("bad.funds@example.com", "Bad Funds");
        await GivenACompletedCustomerProfile("Bad Funds", "Poland");

        await WhenFundsAreAdded(-5);

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task transferring_funds_moves_the_balance_between_wallets()
    {
        await GivenARegisteredUser("sender@example.com", "Sender");
        await GivenACompletedCustomerProfile("Sender", "Poland");
        await GivenFundsWereAdded(200);
        await GivenASecondCustomer("receiver@example.com", "Receiver");

        await WhenFundsAreTransferredToTheSecondCustomer(50);

        ThenTheResponseStatusIs(200);
        ThenTheWalletBalanceIs(150);
        await ThenTheSecondCustomersWalletBalanceIs(50);
    }

    [BobcatSpec]
    public async Task transferring_more_than_the_balance_returns_400()
    {
        await GivenARegisteredUser("broke@example.com", "Broke User");
        await GivenACompletedCustomerProfile("Broke User", "Poland");
        await GivenASecondCustomer("broke.receiver@example.com", "Broke Receiver");

        await WhenFundsAreTransferredToTheSecondCustomer(1000);

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task creating_a_deposit_records_it_as_completed()
    {
        await GivenARegisteredUser("depositor@example.com", "Depositor");
        await GivenACompletedCustomerProfile("Depositor", "Poland");

        await WhenADepositIsCreated(50, "PLN");

        ThenTheResponseStatusIs(200);
        ThenTheDepositIsCompleted();
        ThenTheDepositCurrencyIs("PLN");
    }

    [BobcatSpec]
    public async Task depositing_a_negative_amount_returns_400()
    {
        await GivenARegisteredUser("bad.deposit@example.com", "Bad Depositor");
        await GivenACompletedCustomerProfile("Bad Depositor", "Poland");

        await WhenADepositIsCreated(-1, "PLN");

        ThenTheResponseStatusIs(400);
    }

    // ---- registration -----------------------------------------------------------------------

    [Given("a user registered as \"{email}\" named \"{fullName}\"")]
    public Task GivenARegisteredUser(string email, string fullName) => register(email, fullName);

    [When("a user registers as \"{email}\" named \"{fullName}\"")]
    public Task WhenAUserRegisters(string email, string fullName) => register(email, fullName);

    private async Task register(string email, string fullName)
    {
        var result = await TrackedAsync(() =>
            PostJsonAsync<RegisterUser, User>("/api/users", new RegisterUser(email, fullName, Password)));

        _lastStatusCode = result.StatusCode;

        // The Customer is created by the Customers module in response to the cascaded UserCreated, and
        // carries the same id as the User.
        if (result.Body is not null && result.Body.Id != Guid.Empty && _customerId == Guid.Empty)
            _customerId = result.Body.Id;
    }

    // ---- customer profile -------------------------------------------------------------------

    [Given("the customer profile was completed as \"{name}\" from \"{nationality}\"")]
    public Task GivenACompletedCustomerProfile(string name, string nationality) => completeProfile(_customerId, name, nationality);

    [When("the customer profile is completed as \"{name}\" from \"{nationality}\"")]
    public Task WhenTheCustomerProfileIsCompleted(string name, string nationality) => completeProfile(_customerId, name, nationality);

    [When("the profile of a customer that does not exist is completed")]
    public Task WhenTheProfileOfAnUnknownCustomerIsCompleted() => completeProfile(Guid.NewGuid(), "Nobody", "Nowhere");

    private async Task completeProfile(Guid customerId, string name, string nationality)
    {
        var result = await TrackedAsync(() =>
            PutJsonAsync<CompleteCustomer, Customer>(
                $"/api/customers/{customerId}/complete", new CompleteCustomer(customerId, name, name, nationality)));

        _lastStatusCode = result.StatusCode;
        _customer = result.Body;

        // Completing the profile is what makes the Wallets module create a wallet, so this is the first
        // point at which the owner has one to look up.
        if (result.StatusCode == 200) (_walletId, _wallet) = await loadWallet(customerId);
    }

    [Given("a second customer \"{email}\" named \"{fullName}\"")]
    public async Task GivenASecondCustomer(string email, string fullName)
    {
        var registered = await TrackedAsync(() =>
            PostJsonAsync<RegisterUser, User>("/api/users", new RegisterUser(email, fullName, Password)));

        _secondCustomerId = registered.Body!.Id;

        await TrackedAsync(() =>
            PutJsonAsync<CompleteCustomer, Customer>(
                $"/api/customers/{_secondCustomerId}/complete",
                new CompleteCustomer(_secondCustomerId, fullName, fullName, "Poland")));

        (_secondWalletId, _) = await loadWallet(_secondCustomerId);
    }

    // ---- wallets ----------------------------------------------------------------------------

    [Given("{amount} was added to the wallet")]
    public Task GivenFundsWereAdded(decimal amount) => addFunds(amount);

    [When("{amount} is added to the wallet")]
    public Task WhenFundsAreAdded(decimal amount) => addFunds(amount);

    private async Task addFunds(decimal amount)
    {
        var result = await TrackedAsync(() =>
            PostJsonAsync<AddFunds, Wallet>($"/api/wallets/{_walletId}/funds/add", new AddFunds(_walletId, amount)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _wallet = result.Body;
    }

    [When("{amount} is transferred to the second customer")]
    public async Task WhenFundsAreTransferredToTheSecondCustomer(decimal amount)
    {
        var result = await TrackedAsync(() =>
            PostJsonAsync<TransferFunds, Wallet>(
                "/api/wallets/transfer", new TransferFunds(_walletId, _secondWalletId, amount)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _wallet = result.Body;
    }

    // ---- deposits ---------------------------------------------------------------------------

    [When("a deposit of {amount} {currency} is created")]
    public async Task WhenADepositIsCreated(decimal amount, string currency)
    {
        var result = await TrackedAsync(() =>
            PostJsonAsync<CreateDeposit, Deposit>(
                "/api/payments/deposits", new CreateDeposit(_customerId, currency, amount)));

        _lastStatusCode = result.StatusCode;
        _deposit = result.Body;
    }

    // ---- assertions -------------------------------------------------------------------------

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    [Then("a customer profile exists for that user")]
    public async Task ThenACustomerProfileExistsForThatUser()
    {
        var result = await GetJsonAsync<Customer>($"/api/customers/{_customerId}");
        SpecAssert.Check("status", result.StatusCode, 200);
        SpecAssert.Check("id", result.Body?.Id, _customerId);
    }

    [Then("the customer profile is marked complete")]
    public void ThenTheCustomerProfileIsMarkedComplete()
        => SpecAssert.Check("IsCompleted", _customer?.IsCompleted, true);

    [Then("the customer has {count} wallet")]
    public async Task ThenTheCustomerHasWallets(int count)
    {
        var result = await GetJsonAsync<List<Wallet>>($"/api/wallets/owner/{_customerId}");
        SpecAssert.Check("count", result.Body?.Count ?? 0, count);
    }

    [Then("the wallet balance is {balance}")]
    public void ThenTheWalletBalanceIs(decimal balance) => SpecAssert.Check("balance", _wallet?.Balance, balance);

    [Then("the second customer's wallet balance is {balance}")]
    public async Task ThenTheSecondCustomersWalletBalanceIs(decimal balance)
        => SpecAssert.Check("balance", (await GetJsonAsync<Wallet>($"/api/wallets/{_secondWalletId}")).Body?.Balance, balance);

    [Then("the deposit is completed")]
    public void ThenTheDepositIsCompleted() => SpecAssert.Check("status", _deposit?.Status, DepositStatus.Completed);

    [Then("the deposit currency is {currency}")]
    public void ThenTheDepositCurrencyIs(string currency) => SpecAssert.Check("currency", _deposit?.Currency, currency);

    private async Task<(Guid, Wallet?)> loadWallet(Guid ownerId)
    {
        var wallet = (await GetJsonAsync<List<Wallet>>($"/api/wallets/owner/{ownerId}")).Body?.FirstOrDefault();
        return (wallet?.Id ?? Guid.Empty, wallet);
    }
}
