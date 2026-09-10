using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-4397. Members published in-process -- here, cascaded from another handler -- have no listener, so the
/// listener-keyed back-pressure count never sees them. The per-pipeline count has to, because "has every
/// batch this test caused finished?" is exactly what an integration test harness needs to ask.
/// </summary>
public class batching_pending_counts_for_local_members : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StartEnrollmentsHandler))
                    .IncludeType(typeof(PendingEnrollmentBatchHandler));

                // Well past the time it takes to observe the members as pending, so the batch fires on its
                // trigger time rather than racing the assertions
                opts.BatchMessagesOf<PendingEnrollment>(batching =>
                {
                    batching.BatchSize = 100;
                    batching.TriggerTime = 1.Seconds();
                }).Sequential();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_cascaded_member_is_pending_until_its_batch_handler_has_finished()
    {
        var counts = _host.GetRuntime().BatchingPendingCounts;
        var id = Guid.NewGuid();

        counts.PendingForBatchedMessage<PendingEnrollment>().ShouldBe(0);

        await _host.MessageBus().PublishAsync(new StartEnrollments(id, 3));

        await waitUntil(() => counts.PendingForBatchedMessage<PendingEnrollment>() == 3,
            "all three cascaded members to be counted as pending");

        counts.TotalPendingBatchMembers.ShouldBe(3);

        // The back-pressure view is unchanged: none of these members came through a listener
        var elementQueue = _host.GetRuntime().RoutingFor(typeof(PendingEnrollment)).Routes.Single()
            .As<Wolverine.Runtime.Routing.MessageRoute>().Sender.Destination;
        counts.PendingFor(elementQueue).ShouldBe(0);

        await waitUntil(() => counts.PendingForBatchedMessage<PendingEnrollment>() == 0,
            "the batch to reach its terminal");

        // Zero only once the batch handler has finished -- and while it ran, its members were still pending
        PendingEnrollmentBatchHandler.Finished(id).ShouldBeTrue();
        PendingEnrollmentBatchHandler.PendingSeenWhileExecuting(id).ShouldBe(3);
        counts.TotalPendingBatchMembers.ShouldBe(0);
    }

    private static async Task waitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}");
            }

            await Task.Delay(10);
        }
    }
}

public record StartEnrollments(Guid Id, int Count);

public record PendingEnrollment(Guid Id, int Number);

public static class StartEnrollmentsHandler
{
    public static OutgoingMessages Handle(StartEnrollments command)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < command.Count; i++)
        {
            messages.Add(new PendingEnrollment(command.Id, i));
        }

        return messages;
    }
}

public static class PendingEnrollmentBatchHandler
{
    private static readonly ConcurrentDictionary<Guid, int> _pendingWhileExecuting = new();
    private static readonly ConcurrentDictionary<Guid, bool> _finished = new();

    public static int PendingSeenWhileExecuting(Guid id) => _pendingWhileExecuting.GetValueOrDefault(id);

    public static bool Finished(Guid id) => _finished.ContainsKey(id);

    public static async Task Handle(PendingEnrollment[] enrollments, IWolverineRuntime runtime)
    {
        var id = enrollments[0].Id;
        _pendingWhileExecuting[id] = runtime.As<WolverineRuntime>().BatchingPendingCounts
            .PendingForBatchedMessage<PendingEnrollment>();

        // Long enough that a count released at the start of execution, rather than at the terminal,
        // would be caught by the test's wait for zero
        await Task.Delay(250);

        _finished[id] = true;
    }
}
