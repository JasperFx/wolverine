using Shouldly;
using Xunit;

namespace BuildTests;

// GH-5545: when a supervised run may recreate SQL Server or the Azure Service Bus emulator in the middle of the run.
// The probes and the recreate are fakes here; build/ServiceHealing.cs wires the real ones.

public class detecting_a_degraded_service
{
    readonly ManualTime _time = new();
    readonly ServiceHealthWatch _watch;

    public detecting_a_degraded_service()
    {
        _watch = new ServiceHealthWatch("sqlserver", TimeSpan.FromSeconds(3), _ => throw new NotSupportedException(), _time);
    }

    void probe(double seconds, string problem = null)
    {
        _watch.Record(new ServiceProbe(_time.GetUtcNow(), TimeSpan.FromSeconds(seconds), problem));
        _time.Advance(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void three_slow_probes_in_a_row_are_a_degraded_service()
    {
        probe(7.2);
        probe(9);
        probe(4);

        var verdict = _watch.Verdict();
        verdict.Degraded.ShouldBeTrue();
        verdict.Evidence.ShouldContain("the last 3 probes of sqlserver were all unhealthy");
        verdict.Evidence.ShouldContain("7.2s, over the 3s threshold");
    }

    [Fact]
    public void one_healthy_probe_among_the_last_three_is_not()
    {
        probe(7.2);
        probe(0.01);
        probe(9);

        _watch.Verdict().Degraded.ShouldBeFalse();
    }

    [Fact]
    public void slow_probes_before_a_recovery_do_not_count()
    {
        probe(7.2);
        probe(9);
        probe(8);
        probe(0.01);

        _watch.Verdict().Degraded.ShouldBeFalse();
    }

    [Fact]
    public void a_probe_at_the_threshold_is_healthy()
    {
        probe(3);
        probe(3);
        probe(3);

        _watch.Verdict().Degraded.ShouldBeFalse();
    }

    [Fact]
    public void a_fast_probe_that_found_the_symptom_is_unhealthy()
    {
        probe(0.02, "2 request(s) waiting 5s or more to compile or for a memory grant");
        probe(0.02, "1 request(s) waiting 5s or more to compile or for a memory grant");
        probe(0.02, "3 request(s) waiting 5s or more to compile or for a memory grant");

        var verdict = _watch.Verdict();
        verdict.Degraded.ShouldBeTrue();
        verdict.Evidence.ShouldContain("waiting 5s or more to compile");
    }

    [Fact]
    public void two_probes_are_not_enough()
    {
        probe(30);
        probe(30);

        var verdict = _watch.Verdict();
        verdict.Degraded.ShouldBeFalse();
        verdict.Evidence.ShouldContain("only 2 probe(s)");
    }

    [Fact]
    public void probes_older_than_two_minutes_say_nothing_about_the_service_now()
    {
        probe(30);
        probe(30);
        probe(30);
        _time.Advance(TimeSpan.FromMinutes(3));

        _watch.Verdict().Degraded.ShouldBeFalse();
    }

    [Fact]
    public void a_reset_forgets_every_probe()
    {
        probe(30);
        probe(30);
        probe(30);
        _watch.Reset();

        _watch.Verdict().Degraded.ShouldBeFalse();
    }
}

public class confirming_with_probes_taken_now
{
    readonly ManualTime _time = new();

    [Fact]
    public async Task probes_now_when_the_run_degraded_before_the_background_probe_had_three()
    {
        var probes = 0;
        var watch = new ServiceHealthWatch("asb", TimeSpan.FromSeconds(5), _ =>
        {
            probes++;
            return Task.FromResult(new ServiceProbe(_time.GetUtcNow(), TimeSpan.FromSeconds(40)));
        }, _time) { ConfirmSpacing = TimeSpan.FromMilliseconds(1) };

        var verdict = await watch.Confirm(CancellationToken.None);

        probes.ShouldBe(3);
        verdict.Degraded.ShouldBeTrue();
    }

    [Fact]
    public async Task a_probe_that_throws_is_unhealthy()
    {
        var watch = new ServiceHealthWatch("sqlserver", TimeSpan.FromSeconds(3),
            _ => throw new TimeoutException("Connection Timeout Expired"), _time) { ConfirmSpacing = TimeSpan.FromMilliseconds(1) };

        var verdict = await watch.Confirm(CancellationToken.None);

        verdict.Degraded.ShouldBeTrue();
        verdict.Evidence.ShouldContain("TimeoutException: Connection Timeout Expired");
    }

    [Fact]
    public async Task does_not_probe_again_when_the_background_probe_has_enough()
    {
        var watch = new ServiceHealthWatch("sqlserver", TimeSpan.FromSeconds(3),
            _ => throw new InvalidOperationException("should not probe"), _time);
        for (var i = 0; i < 3; i++) watch.Record(new ServiceProbe(_time.GetUtcNow(), TimeSpan.FromMilliseconds(10)));

        var verdict = await watch.Confirm(CancellationToken.None);

        verdict.Degraded.ShouldBeFalse();
    }
}

public class the_ownership_rule
{
    readonly ServiceHealPolicy _policy = new();

    static readonly ServiceVerdict Degraded = new("sqlserver", true, "the last 3 probes of sqlserver were all unhealthy");
    static readonly ServiceVerdict Healthy = new("sqlserver", false, "the last 3 probes of sqlserver were not all unhealthy");

    [Fact]
    public void a_service_this_run_did_not_start_fresh_is_never_recreated()
    {
        var decision = _policy.Decide("sqlserver", Degraded, () => null);

        decision.Action.ShouldBe(HealAction.Decline);
        decision.Reason.ShouldContain("did not start sqlserver fresh");
    }

    [Fact]
    public void owning_one_service_does_not_own_the_other()
    {
        _policy.Own("asb", "started fresh by this run");

        _policy.Decide("sqlserver", Degraded, () => null).Action.ShouldBe(HealAction.Decline);
    }

    [Fact]
    public void slow_tests_on_a_healthy_service_do_not_recreate_it()
    {
        _policy.Own("sqlserver", "started fresh by this run");

        var decision = _policy.Decide("sqlserver", Healthy, () => null);

        decision.Action.ShouldBe(HealAction.Decline);
        decision.Reason.ShouldContain("do not confirm");
    }

    [Fact]
    public void an_owned_degraded_service_nobody_else_uses_is_recreated()
    {
        _policy.Own("sqlserver", "started fresh by this run");

        var decision = _policy.Decide("sqlserver", Degraded, () => null);

        decision.Action.ShouldBe(HealAction.Heal);
        decision.Heal.ShouldBe(1);
    }

    [Fact]
    public void never_under_another_program_using_it()
    {
        _policy.Own("sqlserver", "started fresh by this run");

        var decision = _policy.Decide("sqlserver", Degraded,
            () => "3 session(s) from 2 other client process(es) are connected to it");

        decision.Action.ShouldBe(HealAction.Abort);
        decision.Reason.ShouldContain("3 session(s) from 2 other client process(es)");
    }

    [Fact]
    public void at_most_twice_in_one_run()
    {
        _policy.Own("sqlserver", "started fresh by this run");
        _policy.RecordHeal("sqlserver").ShouldBe(1);
        _policy.RecordHeal("sqlserver").ShouldBe(2);

        var decision = _policy.Decide("sqlserver", Degraded, () => null);

        decision.Action.ShouldBe(HealAction.Abort);
        decision.Reason.ShouldContain("already recreated 2 time(s) in this run");
    }
}

public class healing_a_service
{
    readonly ManualTime _time = new();
    readonly ServiceHealPolicy _policy = new();
    readonly List<string> _happened = [];
    readonly List<string> _log = [];
    readonly ServiceHealthWatch _watch;
    string _inUse;
    readonly ServiceHealer _healer;

    public healing_a_service()
    {
        _healer = new ServiceHealer(_policy, _ => _inUse, (service, _) =>
        {
            _happened.Add($"recreate {service}");
            return Task.CompletedTask;
        }, _log.Add);

        _watch = new ServiceHealthWatch("sqlserver", TimeSpan.FromSeconds(3),
            _ => Task.FromResult(new ServiceProbe(_time.GetUtcNow(), TimeSpan.FromMilliseconds(5))), _time)
        {
            ConfirmSpacing = TimeSpan.FromMilliseconds(1)
        };
        _healer.Watch(_watch);
    }

    void degrade()
    {
        for (var i = 0; i < 3; i++)
        {
            _watch.Record(new ServiceProbe(_time.GetUtcNow(), TimeSpan.FromSeconds(12)));
            _time.Advance(TimeSpan.FromSeconds(15));
        }
    }

    Task lanes(CancellationToken _)
    {
        _happened.Add("create wolverine_w0..3");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task recreates_then_creates_the_lane_databases_then_resumes()
    {
        _policy.Own("sqlserver", "started fresh by this run");
        degrade();

        var decision = await _healer.HealIfDegraded("sqlserver", "the last 5 tests ran 12x", lanes, CancellationToken.None);

        decision.Action.ShouldBe(HealAction.Heal);
        decision.Heal.ShouldBe(1);
        _happened.ShouldBe(new[] { "recreate sqlserver", "create wolverine_w0..3" });
        _policy.HealsOf("sqlserver").ShouldBe(1);

        // What the old container answered says nothing about the new one
        _watch.Verdict().Evidence.ShouldContain("only 0 probe(s)");
    }

    [Fact]
    public async Task says_so_loudly()
    {
        _policy.Own("sqlserver", "started fresh by this run");
        degrade();

        await _healer.HealIfDegraded("sqlserver", "the last 5 tests ran 12x", lanes, CancellationToken.None);

        _log.ShouldContain(x => x.Contains("RECREATING sqlserver IN THE MIDDLE OF THE RUN (heal 1 of at most 2)"));
        _log.ShouldContain(x => x.Contains("why: the last 5 tests ran 12x"));
        _log.ShouldContain(x => x.Contains("the last 3 probes of sqlserver were all unhealthy"));
    }

    [Fact]
    public async Task does_not_touch_a_service_it_does_not_own()
    {
        degrade();

        var decision = await _healer.HealIfDegraded("sqlserver", "slow", lanes, CancellationToken.None);

        decision.Action.ShouldBe(HealAction.Decline);
        _happened.ShouldBeEmpty();
    }

    [Fact]
    public async Task does_not_touch_a_service_whose_probes_are_healthy()
    {
        _policy.Own("sqlserver", "started fresh by this run");

        var decision = await _healer.HealIfDegraded("sqlserver", "slow", lanes, CancellationToken.None);

        decision.Action.ShouldBe(HealAction.Decline);
        _happened.ShouldBeEmpty();
    }

    [Fact]
    public async Task aborts_rather_than_recreate_it_under_another_program()
    {
        _policy.Own("sqlserver", "started fresh by this run");
        degrade();
        _inUse = "1 session(s) from 1 other client process(es) are connected to it";

        var decision = await _healer.HealIfDegraded("sqlserver", "slow", lanes, CancellationToken.None);

        decision.Action.ShouldBe(HealAction.Abort);
        _happened.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_third_degradation_aborts()
    {
        _policy.Own("sqlserver", "started fresh by this run");

        for (var heal = 1; heal <= 2; heal++)
        {
            degrade();
            (await _healer.HealIfDegraded("sqlserver", "slow", lanes, CancellationToken.None)).Heal.ShouldBe(heal);
        }

        degrade();
        var third = await _healer.HealIfDegraded("sqlserver", "slow", lanes, CancellationToken.None);

        third.Action.ShouldBe(HealAction.Abort);
        _happened.Count(x => x == "recreate sqlserver").ShouldBe(2);
    }

    [Fact]
    public async Task a_heal_the_gate_asks_for_checks_the_rules_again()
    {
        // LocalGate's Track.Heal goes straight to HealNow
        _policy.Own("sqlserver", "started fresh by this run");
        _inUse = "Wolverine.AzureServiceBus.Tests is running (pid 4242)";

        var refused = await Should.ThrowAsync<HealRefusedException>(
            () => _healer.HealNow("sqlserver", "slow", null, lanes, CancellationToken.None));

        refused.Message.ShouldContain("pid 4242");
        _happened.ShouldBeEmpty();
    }
}

public class which_gate_units_may_recreate_sql_server
{
    [Fact]
    public void one_suite_on_sql_server()
    {
        GateHealing.HealsSqlServer([("SqlServerTests", true, false), ("CoreTests", false, false)], out var whyNot)
            .ShouldBeTrue();
        whyNot.ShouldBeNull();
    }

    [Fact]
    public void every_suite_on_sql_server_holds_its_track()
    {
        GateHealing.HealsSqlServer([("PolecatTests", true, true), ("PolecatIncidentService.Tests", true, true)], out _)
            .ShouldBeTrue();
    }

    [Fact]
    public void not_when_a_suite_with_a_database_of_its_own_runs_beside_the_others()
    {
        GateHealing.HealsSqlServer(
            [("PersistenceTests", true, true), ("SqlServerTests", true, false), ("Wolverine.ClaimCheck.SqlServer.Tests", true, false)],
            out var whyNot).ShouldBeFalse();

        whyNot.ShouldContain("SqlServerTests, Wolverine.ClaimCheck.SqlServer.Tests hold no track for it");
    }

    [Fact]
    public void nothing_to_say_without_sql_server()
    {
        GateHealing.HealsSqlServer([("CoreTests", false, false)], out var whyNot).ShouldBeFalse();
        whyNot.ShouldBeNull();
    }

    [Theory]
    [InlineData("sqlserver", "sqlserver")]
    [InlineData("asb-emulator", "asb")]
    [InlineData("asb-emulator-2", null)] // the asb heal recreates both pairs, so only one track carries it
    [InlineData("postgresql", null)]
    [InlineData("rabbitmq", null)]
    public void the_service_a_track_heals(string track, string service)
    {
        GateHealing.ServiceHealedBy(track).ShouldBe(service);
    }
}

/// <summary>A clock the test moves. Timers are the system's: the tests only ever wait a millisecond.</summary>
sealed class ManualTime : TimeProvider
{
    DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
