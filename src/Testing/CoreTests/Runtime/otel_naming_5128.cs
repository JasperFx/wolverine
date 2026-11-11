using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

// GH-5128 (7.0): the meter is named exactly "Wolverine", instrument names are dotted, and ServiceName
// defaults from OTEL_SERVICE_NAME ahead of the entry assembly.
public class otel_naming_5128
{
    [Fact]
    public async Task the_meter_is_named_exactly_wolverine()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "otel-naming-5128";
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Meter.Name.ShouldBe("Wolverine");
        MetricsConstants.MeterName.ShouldBe("Wolverine");

        // The service identity is not lost: it is still the `source` tag on every measurement
        runtime.Options.ServiceName.ShouldBe("otel-naming-5128");
    }

    [Fact]
    public void every_instrument_name_is_dotted()
    {
        var names = new[]
        {
            MetricsConstants.MessagesSent, MetricsConstants.ExecutionTime, MetricsConstants.MessagesSucceeded,
            MetricsConstants.DeadLetterQueue, MetricsConstants.FaultPublishFailures,
            MetricsConstants.EffectiveMessageTime, MetricsConstants.InboxCount, MetricsConstants.OutboxCount,
            MetricsConstants.ScheduledCount, MetricsConstants.DatabaseConnectionCount,
            MetricsConstants.DatabaseConnectionBudget, MetricsConstants.LeasesRenewed, MetricsConstants.LeasesLost,
            MetricsConstants.LeaseCeilingReached, MetricsConstants.DuplicatesSuppressed,
            MetricsConstants.IdempotencyEarlyRotation, MetricsConstants.MessagesReceived,
            MetricsConstants.MessagesFailed, MetricsConstants.RecurringOccurrencesSkipped
        };

        foreach (var name in names)
        {
            name.ShouldStartWith("wolverine.");
            name.ShouldNotContain("-");
        }

        names.Distinct().Count().ShouldBe(names.Length);
    }

    [Fact]
    public void an_explicit_service_name_wins_over_otel_service_name()
    {
        WolverineOptions.ResolveServiceName("Ledgers", "aspire-resource").ShouldBe("Ledgers");
    }

    [Fact]
    public void otel_service_name_is_used_when_no_name_was_set()
    {
        WolverineOptions.ResolveServiceName(null, "aspire-resource").ShouldBe("aspire-resource");
        WolverineOptions.ResolveServiceName("", " aspire-resource ").ShouldBe("aspire-resource");
    }

    [Fact]
    public void falls_through_to_the_entry_assembly_when_neither_is_set()
    {
        // null means "use JasperFx's name", which is the entry assembly as before
        WolverineOptions.ResolveServiceName(null, null).ShouldBeNull();
        WolverineOptions.ResolveServiceName(null, "   ").ShouldBeNull();
    }

    [Fact]
    public void the_variable_is_the_standard_opentelemetry_one()
    {
        WolverineOptions.OtelServiceNameVariable.ShouldBe("OTEL_SERVICE_NAME");
    }

    // The GH-4448 exchange with JasperFx, with OTEL_SERVICE_NAME in play. The variable is passed in rather than
    // set on the process, because tests run in parallel and other hosts would pick it up.
    private static JasperFxOptions jasperFxNamedByDefault()
    {
        var jasperfx = new JasperFxOptions { ApplicationAssembly = typeof(otel_naming_5128).Assembly };
        jasperfx.ServiceName = typeof(otel_naming_5128).Assembly.GetName().Name!;
        return jasperfx;
    }

    [Fact]
    public void otel_service_name_replaces_the_entry_assembly_default_and_reaches_jasperfx()
    {
        var jasperfx = jasperFxNamedByDefault();
        var options = new WolverineOptions();

        options.ReadJasperFxOptions(jasperfx, "aspire-res");

        options.ServiceName.ShouldBe("aspire-res");
        jasperfx.ServiceName.ShouldBe("aspire-res");
    }

    [Fact]
    public void an_explicit_wolverine_name_beats_otel_service_name_on_both_sides()
    {
        var jasperfx = jasperFxNamedByDefault();
        var options = new WolverineOptions { ServiceName = "Ledgers" };

        options.ReadJasperFxOptions(jasperfx, "aspire-res");

        options.ServiceName.ShouldBe("Ledgers");
        jasperfx.ServiceName.ShouldBe("Ledgers");
    }

    [Fact]
    public void an_explicit_jasperfx_name_beats_otel_service_name()
    {
        var jasperfx = jasperFxNamedByDefault();
        jasperfx.ServiceName = "Special";
        var options = new WolverineOptions();

        options.ReadJasperFxOptions(jasperfx, "aspire-res");

        options.ServiceName.ShouldBe("Special");
        jasperfx.ServiceName.ShouldBe("Special");
    }

    [Fact]
    public void without_otel_service_name_the_entry_assembly_default_stands()
    {
        var jasperfx = jasperFxNamedByDefault();
        var options = new WolverineOptions();

        options.ReadJasperFxOptions(jasperfx, null);

        options.ServiceName.ShouldBe(typeof(otel_naming_5128).Assembly.GetName().Name);
    }
}
