using JasperFx.Events;
using Shouldly;
using Wolverine.Persistence.EventSourcing;
using Xunit;

namespace CoreTests;

#region sample_stub_event_stream_handler

public record Account(decimal Balance, bool IsFrozen);

public record Withdraw(Guid AccountId, decimal Amount);

public record FundsWithdrawn(decimal Amount);

public record WithdrawalRejected(string Reason);

public static class WithdrawHandler
{
    public static void Handle(Withdraw command, [WriteModel] IEventStream<Account> stream)
    {
        var account = stream.Aggregate;

        if (account is null || account.IsFrozen)
        {
            stream.AppendOne(new WithdrawalRejected("Account is not available"));
            return;
        }

        if (account.Balance < command.Amount)
        {
            stream.AppendOne(new WithdrawalRejected("Insufficient funds"));
            return;
        }

        stream.AppendOne(new FundsWithdrawn(command.Amount));
    }
}

#endregion

public class unit_testing_aggregate_handlers
{
    #region sample_stub_event_stream_happy_path

    [Fact]
    public void withdraws_when_the_funds_are_there()
    {
        // Arrange -- the aggregate state the handler should see. No host, no database, no mocks.
        var stream = new StubEventStream<Account>(new Account(500m, false));

        // Act -- just call the handler. It is a static method taking the stream.
        WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

        // Assert on the events the handler decided to append
        stream.EventsAppended.Single()
            .ShouldBeOfType<FundsWithdrawn>()
            .Amount.ShouldBe(100m);
    }

    #endregion

    #region sample_stub_event_stream_missing_stream

    [Fact]
    public void rejects_the_withdrawal_when_the_stream_does_not_exist_yet()
    {
        // A null aggregate is a stream that has not been started
        var stream = new StubEventStream<Account>(null);

        WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

        stream.EventsAppended.Single()
            .ShouldBeOfType<WithdrawalRejected>()
            .Reason.ShouldBe("Account is not available");
    }

    #endregion

    [Fact]
    public void rejects_the_withdrawal_when_the_funds_are_short()
    {
        var stream = new StubEventStream<Account>(new Account(50m, false));

        WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

        stream.EventsAppended.Single()
            .ShouldBeOfType<WithdrawalRejected>()
            .Reason.ShouldBe("Insufficient funds");
    }

    #region sample_stub_event_stream_versions

    [Fact]
    public void the_versions_are_settable_for_a_version_sensitive_handler()
    {
        var stream = new StubEventStream<Account>(new Account(500m, false))
        {
            StartingVersion = 4,
            CurrentVersion = 4
        };

        WithdrawHandler.Handle(new Withdraw(stream.Id, 100m), stream);

        // The stub records; it does not advance the version the way a real
        // store would at save time
        stream.CurrentVersion.ShouldBe(4);
    }

    #endregion
}
