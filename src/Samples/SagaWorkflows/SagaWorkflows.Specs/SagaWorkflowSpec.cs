using Bobcat;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SagaWorkflows.Providers;
using Wolverine;
using Wolverine.Bobcat;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Util;

namespace SagaWorkflows.Specs;

/// <summary>
/// The base of every specification here: acts in the workflows service, also tracks the providers service, and treats
/// a dead letter as an outcome to judge rather than a failed act.
/// </summary>
public abstract class SagaWorkflowSpec : WolverineSpec, IAsyncLifetime
{
    private readonly WorkflowServices _services;

    protected SagaWorkflowSpec(WorkflowServices services) : base(services.Workflows)
    {
        _services = services;

        AlsoTrack(services.Providers);
        DeadLettersFailTheAct = false;

        // Retries in the providers service, hops over TCP and back, a saga load and a compensation in workflows
        Timeout = TimeSpan.FromSeconds(30);
    }

    public async ValueTask InitializeAsync() => await _services.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Bank Bank => _services.Providers.Services.GetRequiredService<Bank>();

    // ---- the bank's state ---------------------------------------------------------------------------------------------

    [Given("account {account} holds {balance}")]
    public void AccountHolds(string account, decimal balance) => Bank.Open(account, balance);

    [Given("account {account} is closed")]
    public void AccountIsClosed(string account) => Bank.ClosedAccounts.Add(account);

    [Given("the ledger refuses to record transfer {transferId}")]
    public void TheLedgerRefuses(Guid transferId) => Bank.LedgerRefuses.Add(transferId);

    [Given("large transfers from {account} look fraudulent")]
    public void FraudSuspected(string account) => Bank.FraudSuspects.Add(account);

    [Then("account {account} holds {balance}")]
    public void ThenAccountHolds(string account, decimal balance)
        => SpecAssert.Check("balance", Bank.BalanceOf(account), balance);

    // ---- the workflows service's schedule -----------------------------------------------------------------------------

    /// <summary>
    /// GH-5674. How many <paramref name="messageType" /> messages the saga <paramref name="sagaId" /> has waiting in the
    /// workflows service's durable schedule.
    /// </summary>
    [Then("{count} {messageType} of saga {sagaId} waiting in the schedule")]
    public async Task ThenWaitingInTheSchedule(Type messageType, object sagaId, int count)
    {
        var storage = _services.Workflows.Services.GetRequiredService<IWolverineRuntime>().Storage;
        var results = await storage.ScheduledMessages.QueryAsync(new ScheduledMessageQuery
        {
            SagaId = sagaId.ToString(),
            MessageType = messageType.ToMessageTypeName()
        }, CancellationToken.None);

        SpecAssert.Check("waiting", results.TotalCount, count);
    }

    /// <summary>The act sent exactly <paramref name="times" /> messages of <paramref name="messageType" />.</summary>
    [Then("exactly {times} {messageType} sent")]
    public void ThenSentExactly(Type messageType, int times)
        => SpecAssert.Check("sent", TheMessagesSent.Count(messageType.IsInstanceOfType), times);

    // ---- what the workflows service says about its sagas --------------------------------------------------------------

    /// <summary>
    /// GH-5672. The service's description (what <c>describe</c> and CritterWatch read) lists the <c>Compensate</c>
    /// method of <typeparamref name="TSaga" /> for <paramref name="failedMessageType" /> as compensating exactly
    /// <paramref name="messageTypes" />.
    /// </summary>
    [Then("the Compensate method for {failedMessageType} compensates {messageTypes}")]
    public async Task ThenTheCompensateMethodCovers<TSaga>(Type failedMessageType, params Type[] messageTypes)
        where TSaga : Saga
    {
        var capabilities = await ServiceCapabilities.ReadFrom(
            _services.Workflows.Services.GetRequiredService<IWolverineRuntime>(), null, CancellationToken.None);

        var saga = capabilities.Sagas.SingleOrDefault(x => x.StateType.FullName == typeof(TSaga).FullName);
        var compensation = saga?.Compensations.FirstOrDefault(x => x.FailedMessageType.FullName == failedMessageType.FullName);

        SpecAssert.Fact(compensation is not null,
            $"Expected {typeof(TSaga).Name} to describe a Compensate method for {failedMessageType.Name}, but it describes " +
            (saga is null ? "no such saga" : string.Join(", ", saga.Compensations.Select(x => x.FailedMessageType.Name))));

        var covered = compensation?.Covers.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray() ?? [];
        SpecAssert.Check("compensates", string.Join(", ", covered),
            string.Join(", ", messageTypes.Select(x => x.NameInCode()).Order(StringComparer.Ordinal)));
    }
}
