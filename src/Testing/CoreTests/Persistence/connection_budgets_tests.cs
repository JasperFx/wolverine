using JasperFx.Descriptors;
using Shouldly;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence;

public class connection_budgets_tests
{
    private readonly ConnectionBudgets theBudgets = new();

    [Fact]
    public void no_budget_for_an_unknown_server()
    {
        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-a", 5432)).ShouldBeNull();
        theBudgets.HasAny.ShouldBeFalse();
    }

    [Fact]
    public void find_the_budget_for_a_server_by_host_and_port()
    {
        theBudgets
            .ForServer("shard-a", 5432, 400)
            .ForServer("shard-b", 5432, 200);

        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-a", 5432)).ShouldBe(400);
        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-b", 5432)).ShouldBe(200);
    }

    [Fact]
    public void the_port_is_part_of_the_key()
    {
        // Two clusters co-hosted on one box. Keying on the host alone would collide them onto a
        // single budget — the gap that made the port part of DatabaseServerId in the first place.
        theBudgets
            .ForServer("localhost", 5432, 400)
            .ForServer("localhost", 5433, 100);

        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "localhost", 5432)).ShouldBe(400);
        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "localhost", 5433)).ShouldBe(100);
    }

    [Fact]
    public void host_names_are_matched_case_insensitively()
    {
        theBudgets.ForServer("Shard-A", 5432, 400);

        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-a", 5432)).ShouldBe(400);
    }

    [Fact]
    public void a_portless_registration_covers_the_whole_host()
    {
        // How SQL Server registers: its Data Source already carries the port or named instance, so
        // DatabaseServerId leaves Port null.
        theBudgets.ForServer("sql-1,1433", 500);

        theBudgets.MaxFor(new DatabaseServerId("SqlServer", "sql-1,1433", null)).ShouldBe(500);
    }

    [Fact]
    public void an_exact_match_beats_a_host_wide_registration()
    {
        theBudgets
            .ForServer("shard-a", 250)
            .ForServer("shard-a", 5432, 400);

        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-a", 5432)).ShouldBe(400);

        // ...and the host-wide budget still covers the other ports on that host.
        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-a", 5433)).ShouldBe(250);
    }

    [Fact]
    public void the_last_registration_for_a_server_wins()
    {
        theBudgets
            .ForServer("shard-a", 5432, 400)
            .ForServer("shard-a", 5432, 150);

        theBudgets.MaxFor(new DatabaseServerId("PostgreSQL", "shard-a", 5432)).ShouldBe(150);
    }

    [Fact]
    public void reject_a_nonsense_budget()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => theBudgets.ForServer("shard-a", 5432, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => theBudgets.ForServer("shard-a", 5432, -1));
        Should.Throw<ArgumentException>(() => theBudgets.ForServer("", 5432, 100));
    }

    [Fact]
    public void off_by_default_for_a_single_database()
    {
        // A per-server budget tells you nothing a per-database count doesn't when there's only the
        // one database. No probe, no cost.
        theBudgets.IsActive(DatabaseCardinality.Single).ShouldBeFalse();
        theBudgets.IsActive(DatabaseCardinality.None).ShouldBeFalse();
    }

    [Fact]
    public void on_for_the_sharded_shape()
    {
        // Marten's MultiTenantedWithShardedDatabases — many databases, few servers. The shape the
        // whole feature exists for.
        theBudgets.IsActive(DatabaseCardinality.StaticMultiple).ShouldBeTrue();
    }

    [Fact]
    public void declaring_a_budget_is_itself_an_opt_in()
    {
        theBudgets.ForServer("shard-a", 5432, 400);

        theBudgets.IsActive(DatabaseCardinality.Single).ShouldBeTrue();
    }

    [Fact]
    public void an_explicit_setting_overrides_the_automatic_rule_in_both_directions()
    {
        theBudgets.Enabled = false;
        theBudgets.ForServer("shard-a", 5432, 400);
        theBudgets.IsActive(DatabaseCardinality.StaticMultiple).ShouldBeFalse();

        theBudgets.Enabled = true;
        theBudgets.IsActive(DatabaseCardinality.Single).ShouldBeTrue();
    }
}

public class database_server_id_tests
{
    [Fact]
    public void the_port_makes_co_hosted_clusters_distinct()
    {
        new DatabaseServerId("PostgreSQL", "localhost", 5432)
            .ShouldNotBe(new DatabaseServerId("PostgreSQL", "localhost", 5433));
    }

    [Fact]
    public void render_host_and_port_for_the_metric_tag()
    {
        new DatabaseServerId("PostgreSQL", "shard-a", 5432).ToString().ShouldBe("shard-a:5432");
    }

    [Fact]
    public void render_the_bare_server_name_when_the_port_is_folded_into_it()
    {
        new DatabaseServerId("SqlServer", @"sql-1\SQLEXPRESS", null).ToString().ShouldBe(@"sql-1\SQLEXPRESS");
    }
}

// GH-5148. The matching the .NET 11 startup validator uses to refuse a budget that names no known server.
public class connection_budgets_unmatched_servers_tests
{
    private readonly ConnectionBudgets theBudgets = new();

    [Fact]
    public void nothing_declared_means_nothing_unmatched()
    {
        theBudgets.UnmatchedServers([new DatabaseServerId("PostgreSQL", "shard-a", 5432)]).ShouldBeEmpty();
    }

    [Fact]
    public void exact_host_and_port_match()
    {
        theBudgets.ForServer("shard-a", 5432, 400);

        theBudgets.UnmatchedServers([new DatabaseServerId("PostgreSQL", "shard-a", 5432)]).ShouldBeEmpty();
    }

    [Fact]
    public void the_port_must_match_when_one_was_declared()
    {
        theBudgets.ForServer("shard-a", 5433, 400);

        theBudgets.UnmatchedServers([new DatabaseServerId("PostgreSQL", "shard-a", 5432)])
            .ShouldBe(["shard-a:5433"]);
    }

    [Fact]
    public void host_wide_budget_matches_any_port()
    {
        theBudgets.ForServer("shard-a", 400);

        theBudgets.UnmatchedServers([new DatabaseServerId("PostgreSQL", "shard-a", 5432)]).ShouldBeEmpty();
    }

    [Fact]
    public void server_names_compare_case_insensitively_and_ignore_the_engine()
    {
        theBudgets.ForServer("SQL01,1433", 200);

        theBudgets.UnmatchedServers([new DatabaseServerId("SqlServer", "sql01,1433", null)]).ShouldBeEmpty();
    }

    [Fact]
    public void reports_every_unmatched_budget()
    {
        theBudgets
            .ForServer("shard-a", 5432, 400)
            .ForServer("typo-host", 5432, 100)
            .ForServer("old-host", 50);

        theBudgets.UnmatchedServers([new DatabaseServerId("PostgreSQL", "shard-a", 5432)])
            .ShouldBe(["typo-host:5432", "old-host"]);
    }

    [Fact]
    public void no_known_servers_leaves_every_budget_unmatched()
    {
        theBudgets.ForServer("shard-a", 5432, 400);

        theBudgets.UnmatchedServers([]).ShouldBe(["shard-a:5432"]);
    }
}
