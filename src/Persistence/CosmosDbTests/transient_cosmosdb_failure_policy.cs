using System.Net;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Shouldly;
using Wolverine.CosmosDb;
using Wolverine.ErrorHandling;

namespace CosmosDbTests;

public class transient_cosmosdb_failure_policy
{
    private static CosmosException Transient() => CosmosError(HttpStatusCode.TooManyRequests);
    private static CosmosException Permanent() => CosmosError(HttpStatusCode.Conflict);

    private static CosmosException CosmosError(HttpStatusCode status, int subStatus = 0) =>
        new($"Status {(int)status}/{subStatus}", status, subStatus, Guid.NewGuid().ToString(), 1);

    [Theory]
    [InlineData(408, 0)] // request timeout
    [InlineData(429, 0)] // too many requests
    [InlineData(429, 3200)] // too many requests, RU budget exceeded
    [InlineData(449, 0)] // retry with
    [InlineData(503, 0)] // service unavailable
    [InlineData(410, 0)] // gone
    [InlineData(410, 1000)] // gone, name cache is stale
    [InlineData(410, 1002)] // gone, partition key range gone
    [InlineData(410, 1007)] // gone, completing split
    [InlineData(410, 1008)] // gone, completing partition migration
    [InlineData(404, 1002)] // read session not available
    public void transient_failures_match(int status, int subStatus)
    {
        TransientCosmosDbFailure.Is(CosmosError((HttpStatusCode)status, subStatus)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(400, 0)] // bad request
    [InlineData(401, 0)] // unauthorized
    [InlineData(403, 0)] // forbidden
    [InlineData(404, 0)] // not found
    [InlineData(409, 0)] // conflict, the id already exists
    [InlineData(410, 1022)] // gone, lease not found
    [InlineData(412, 0)] // precondition failed, the etag changed
    [InlineData(413, 0)] // entity too large
    [InlineData(500, 0)] // internal server error
    public void permanent_failures_do_not_match(int status, int subStatus)
    {
        TransientCosmosDbFailure.Is(CosmosError((HttpStatusCode)status, subStatus)).ShouldBeFalse();
    }

    [Fact]
    public void wrapped_exceptions_are_unwrapped()
    {
        TransientCosmosDbFailure.Is(new InvalidOperationException("outer", new Exception("middle", Transient())))
            .ShouldBeTrue();
        TransientCosmosDbFailure.Is(new InvalidOperationException("outer", new Exception("middle", Permanent())))
            .ShouldBeFalse();
    }

    [Fact]
    public void unrelated_exceptions_do_not_match()
    {
        TransientCosmosDbFailure.Is(null).ShouldBeFalse();
        TransientCosmosDbFailure.Is(new InvalidOperationException()).ShouldBeFalse();
        TransientCosmosDbFailure.Is(new TimeoutException()).ShouldBeFalse();
    }

    [Fact]
    public void it_registers_one_rule_with_the_chosen_action()
    {
        var policies = new FailurePoliciesHolder(new FailureRuleCollection());

        policies.OnTransientCosmosDbFailure().ScheduleRetry(1.Seconds(), 5.Seconds());

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
