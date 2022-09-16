using System;
using System.Linq;
using System.Threading.Tasks;
using Baseline.Dates;
using NSubstitute;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.ErrorHandling;

public class CircuitBreakerTests
{
    protected readonly IListeningAgent theAgent = Substitute.For<IListeningAgent>();

    protected readonly CircuitBreakerOptions theOptions = new()
    {
        TrackingPeriod = 4.Minutes(),
        FailurePercentageThreshold = 10,
        MinimumThreshold = 10,
        PauseTime = 5.Minutes()
    };

    private CircuitBreaker _breaker;

    internal IExceptionMatch theExceptionMatch = new AlwaysMatches();
    private DateTimeOffset theStartingTime;

    internal CircuitBreaker theBreaker
    {
        get
        {
            _breaker ??= new CircuitBreaker(theOptions, theAgent);

            return _breaker;
        }
    }

    private void assertNoPause()
    {
        theAgent.DidNotReceiveWithAnyArgs().PauseAsync(theOptions.PauseTime);
    }

    private ValueTask assertThatTheListenerWasPaused()
    {
        return theAgent.Received().PauseAsync(theOptions.PauseTime);
    }

    internal ValueTask initialUpdateOfTotals(int failures, int total)
    {
        return theBreaker.UpdateTotalsAsync(theStartingTime, failures, total);
    }

    internal ValueTask subsequentUpdateOfTotals(TimeSpan later, int failures, int total)
    {
        return theBreaker.UpdateTotalsAsync(theStartingTime.Add(later), failures, total);
    }

    [Fact]
    public void set_the_generation_period_to_quarter_of_tracking_period()
    {
        var options = new CircuitBreakerOptions
        {
            TrackingPeriod = 4.Minutes()
        };

        var breaker = new CircuitBreaker(
            options,
            Substitute.For<IListeningAgent>());

        ShouldBeTestExtensions.ShouldBe(breaker.GenerationPeriod, 1.Minutes());
    }

    [Fact]
    public void set_initial_time()
    {
        theBreaker.DetermineGeneration(theStartingTime);

        var generation = theBreaker.CurrentGenerations.Single<CircuitBreaker.Generation>();

        generation.Start.ShouldBe(theStartingTime);
        generation.Expires.ShouldBe(theStartingTime.Add(theOptions.TrackingPeriod));
        generation.End.ShouldBe(theStartingTime.Add(theBreaker.GenerationPeriod));
    }

    [Fact]
    public void set_time_within_first_generation()
    {
        theBreaker.DetermineGeneration(theStartingTime);
        theBreaker.DetermineGeneration(theStartingTime.Add(30.Seconds()));

        theBreaker.CurrentGenerations.Count.ShouldBe(1);
    }

    [Fact]
    public void set_time_at_end_of_first_generation()
    {
        theBreaker.DetermineGeneration(theStartingTime);
        theBreaker.DetermineGeneration(theStartingTime.Add(1.Minutes()));

        // IT'S non-exclusive to the end
        theBreaker.CurrentGenerations.Count.ShouldBe(2);
    }

    [Fact]
    public void set_time_after_first_generation()
    {
        theBreaker.DetermineGeneration(theStartingTime);
        theBreaker.DetermineGeneration(theStartingTime.Add(theBreaker.GenerationPeriod).Add(1.Seconds()));

        theBreaker.CurrentGenerations.Count.ShouldBe(2);
    }

    [Fact]
    public void clear_expired_generations_on_set_time()
    {
        theBreaker.DetermineGeneration(theStartingTime);
        theBreaker.DetermineGeneration(theStartingTime.Add(theBreaker.GenerationPeriod).Add(1.Seconds()));
        theBreaker.DetermineGeneration(theStartingTime.Add(2.Minutes()).Add(2.Seconds()));
        theBreaker.DetermineGeneration(theStartingTime.Add(3.Minutes()).Add(3.Seconds()));

        var starting = theBreaker.CurrentGenerations.ToArray();
        starting.Length.ShouldBe(4);

        var endingTime = starting[2].Expires.AddSeconds(3);
        theBreaker.DetermineGeneration(endingTime);

        var ending = theBreaker.CurrentGenerations;
        ending.ShouldNotContain(starting[0]);
        ending.ShouldNotContain(starting[1]);

        ending.Last().IsActive(endingTime).ShouldBeTrue();
    }

    [Fact]
    public async Task initial_update_starts_first_generation()
    {
        await initialUpdateOfTotals(1, 10);
        var generation = theBreaker.CurrentGenerations.Single();

        generation.Failures.ShouldBe(1);
        generation.Total.ShouldBe(10);
    }


    [Fact]
    public async Task first_totals_update_with_failures_but_not_met_threshold()
    {
        await initialUpdateOfTotals(5, 9);
        assertNoPause();
    }

    [Fact]
    public async Task initial_totals_with_no_failures()
    {
        await initialUpdateOfTotals(0, 10);
        assertNoPause();
    }

    [Fact]
    public async Task initial_totals_has_enough_samples_to_trip_breaker()
    {
        await initialUpdateOfTotals(20, 100);
        await assertThatTheListenerWasPaused();
    }

    [Fact]
    public async Task trip_off_with_enough_failures_after_initial_set()
    {
        await initialUpdateOfTotals(2, 5);

        var time = 1;
        var random = new Random();

        for (var i = 0; i < 100; i++)
        {
            time += random.Next(1, 10);
            await subsequentUpdateOfTotals(time.Seconds(), 5, 100);
        }

        assertNoPause();

        time++;
        await subsequentUpdateOfTotals(time.Seconds(), 1000, 1000);

        await assertThatTheListenerWasPaused();
    }

    [Fact]
    public async Task run_over_time_not_pausing()
    {
        await initialUpdateOfTotals(2, 5);

        var time = 1;
        var random = new Random();

        for (var i = 0; i < 10000; i++)
        {
            time += random.Next(1, 10);
            await subsequentUpdateOfTotals(time.Seconds(), 5, 100);
        }

        assertNoPause();
    }
}
