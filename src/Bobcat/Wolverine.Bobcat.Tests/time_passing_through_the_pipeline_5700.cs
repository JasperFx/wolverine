using Bobcat;
using Bobcat.Runtime;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;

namespace Wolverine.Bobcat.Tests;

// GH-5700: WhenTimePasses delivers a due message to its queue as if its time had come, so the endpoint's retries,
// dead-lettering and a saga's Compensate methods apply to it, just as in production. "Time passes, the poll fails, the
// saga compensates" is a spec. playInline: true runs it inline instead, where the first failure fails the act.

public record StartExportPolling(Guid Id);

public record PollExportStatus(Guid Id) : TimeoutMessage(5.Minutes());

public record ExportPollingGaveUp(Guid Id, string Reason);

public class ExportServiceDown() : Exception("The export service is down");

// Whether the outside export service answers. A singleton the spec flips
public class ExportService
{
    public bool Down { get; set; }
}

public class ExportPolling : CompensatingSaga
{
    public Guid Id { get; set; }
    public int Polls { get; set; }

    public static (ExportPolling, PollExportStatus) Start(StartExportPolling command)
        => (new ExportPolling { Id = command.Id }, new PollExportStatus(command.Id));

    public PollExportStatus Handle(PollExportStatus poll, ExportService service)
    {
        if (service.Down) throw new ExportServiceDown();

        Polls++;
        return new PollExportStatus(Id);
    }

    // The poll failed after its retries: stop polling and say so
    public ExportPollingGaveUp Compensate(PollExportStatus failed, ExportServiceDown e)
    {
        MarkCompleted();
        return new ExportPollingGaveUp(Id, e.Message);
    }
}

public static class ExportPollingGaveUpHandler
{
    public static void Handle(ExportPollingGaveUp gaveUp)
    {
    }
}

public sealed class ExportPollingHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ExportPolling))
                    .IncludeType(typeof(ExportPollingGaveUpHandler));

                opts.Services.AddSingleton<ExportService>();
                opts.Services.ShareClock();

                // Two retries, then the saga compensates
                opts.OnException<ExportServiceDown>().RetryTimes(2);
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

[CollectionDefinition(nameof(ExportPollingCollection))]
public class ExportPollingCollection : ICollectionFixture<ExportPollingHost>;

// The steps about the outside export service
public abstract class ExportPollingSpec(ExportPollingHost app) : WolverineSpec(app.Host)
{
    private ExportService Exports => app.Host.Services.GetRequiredService<ExportService>();

    [Given("the export service is down")]
    public void TheExportServiceIsDown() => Exports.Down = true;

    [Given("the export service is up")]
    public void TheExportServiceIsUp() => Exports.Down = false;
}

[Collection(nameof(ExportPollingCollection))]
public class time_passing_through_the_pipeline(ExportPollingHost app) : ExportPollingSpec(app)
{
    private readonly Guid _id = Guid.NewGuid();

    [Fact]
    public async Task time_passes_the_poll_fails_and_the_saga_compensates()
    {
        DeadLettersFailTheAct = false;
        TheExportServiceIsUp();
        await WhenReceived(new StartExportPolling(_id));

        TheExportServiceIsDown();
        await WhenTimePasses(5.Minutes());

        // Tried three times under the endpoint's retry policy, then compensated
        ThenMessageSent(new ExportPollingGaveUp(_id, "The export service is down"));
        ThenHandled<ExportPollingGaveUp>();
        await ThenNoSaga<ExportPolling>(_id);
    }

    [Fact]
    public async Task a_poll_that_works_keeps_polling()
    {
        TheExportServiceIsUp();
        await WhenReceived(new StartExportPolling(_id));

        await WhenTimePasses(5.Minutes());

        ThenHandled<PollExportStatus>();
        ThenMessageScheduled<PollExportStatus>(5.Minutes());
        ThenNoMessageSent<ExportPollingGaveUp>();
    }

    // Inline, as before 7.0: the failure fails the act, and nothing compensates
    [Fact]
    public async Task played_inline_the_first_failure_fails_the_act()
    {
        TheExportServiceIsUp();
        await WhenReceived(new StartExportPolling(_id));

        TheExportServiceIsDown();
        await WhenTimePasses(5.Minutes(), playInline: true);

        ThenRefusedWith("The export service is down");
        ThenNoMessageSent<ExportPollingGaveUp>();
    }
}
