using System;
using JasperFx;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Persistence.Sagas;

// GH-3444: SagaConcurrencyException inherits JasperFx.ConcurrencyException so a single
// OnException<ConcurrencyException>() policy catches saga concurrency failures across every storage
// provider. Marten already surfaces JasperFx's type; the EF Core / lightweight / CosmosDb saga paths
// throw SagaConcurrencyException. The docs (durability/sagas.md) tell users to write exactly that policy.
public class saga_concurrency_exception_tests
{
    [Fact]
    public void is_a_jasperfx_concurrency_exception()
    {
        new SagaConcurrencyException("stale").ShouldBeAssignableTo<ConcurrencyException>();
    }

    [Fact]
    public void a_registered_concurrency_exception_policy_would_match_it()
    {
        // The catch a user's OnException<ConcurrencyException>() compiles to
        Exception thrown = new SagaConcurrencyException("stale");
        (thrown is ConcurrencyException).ShouldBeTrue();
    }

    [Fact]
    public void preserves_the_inner_store_exception()
    {
        // The CosmosDb saga path attaches the 412 Precondition Failed as the inner exception; inheriting
        // ConcurrencyException must not lose it (JasperFx 2.28.0 added the (string, Exception) base ctor
        // that makes this possible — before it, InnerException could not be set at all).
        var inner = new InvalidOperationException("412 Precondition Failed");
        var ex = new SagaConcurrencyException("Saga was modified concurrently", inner);

        ex.InnerException.ShouldBeSameAs(inner);
        ex.Message.ShouldBe("Saga was modified concurrently");
    }
}
