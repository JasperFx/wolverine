using JasperFx.Core;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Cluster;
using Raven.Client.Exceptions.Database;
using Raven.Client.Exceptions.Documents;
using Raven.Client.Exceptions.Documents.Compilation;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.RavenDb;

namespace RavenDbTests;

public class transient_ravendb_failure_policy
{
    private static Exception Transient() => new AllTopologyNodesDownException("all nodes are down");
    private static Exception Permanent() => new ConcurrencyException("optimistic concurrency violation");

    public static IEnumerable<object[]> TransientFailures() =>
    [
        [new AllTopologyNodesDownException("all nodes are down")],
        [new RequestedNodeUnavailableException("node B is unavailable")],
        [new UnsuccessfulRequestException("http://node-a")],
        [new RavenTimeoutException("timed out")],
        [new NodeIsPassiveException("passive")],
        [new NoLeaderException("no leader")],
        [new DatabaseLoadTimeoutException("still loading")],
        [new DatabaseConcurrentLoadTimeoutException("too many databases loading")],
        [new DatabaseIdleException("idle")]
    ];

    public static IEnumerable<object[]> PermanentFailures() =>
    [
        [new ConcurrencyException("optimistic concurrency violation")],
        [new ClusterTransactionConcurrencyException("cluster transaction conflict")],
        [new DocumentConflictException("conflict", "users/1", 0)],
        [new DatabaseDoesNotExistException("no such database")],
        [new DatabaseDisabledException("disabled")],
        [new IndexCompilationException("bad index")],
        [new InvalidQueryException("bad query")],
        [new BadRequestException("bad request")]
    ];

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public void transient_failures_match(Exception exception)
    {
        TransientRavenDbFailure.Is(exception).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(PermanentFailures))]
    public void permanent_failures_do_not_match(Exception exception)
    {
        TransientRavenDbFailure.Is(exception).ShouldBeFalse();
    }

    [Fact]
    public void wrapped_exceptions_are_unwrapped()
    {
        TransientRavenDbFailure.Is(new InvalidOperationException("outer", new Exception("middle", Transient())))
            .ShouldBeTrue();
        TransientRavenDbFailure.Is(new InvalidOperationException("outer", new Exception("middle", Permanent())))
            .ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientRavenDbFailure.Is(null).ShouldBeFalse();
        TransientRavenDbFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientRavenDbFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientRavenDbFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

        var rule = policies.Failures.ShouldHaveSingleItem();
        rule.Match.Matches(Transient()).ShouldBeTrue();
        rule.Match.Matches(Permanent()).ShouldBeFalse();
        rule.Count().ShouldBe(2);
    }

    private class FailurePoliciesHolder(FailureRuleCollection failures) : IWithFailurePolicies
    {
        public FailureRuleCollection Failures { get; } = failures;
    }
}
