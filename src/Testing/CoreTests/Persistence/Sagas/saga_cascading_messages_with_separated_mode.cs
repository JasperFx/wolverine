using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Sagas;

public class saga_cascading_messages_with_separated_mode
{
    [Fact]
    public async Task cascading_message_should_have_saga_id_attached()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CascadingTestSaga))
                    .IncludeType(typeof(ExternalHandler));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            }).StartAsync();

        var id = Guid.NewGuid();
        ExternalHandler.CascadedMessageCount = 0;

        // Start the saga
        await host.InvokeMessageAndWaitAsync(new StartCascadingTest(id));

        // Send message that triggers cascading - this should work without IndeterminateSagaStateIdException
        var tracked = await host.SendMessageAndWaitAsync(new TriggerCascade(id));

        // Verify the cascading message was executed by both saga and external handler
        var cascadedEnvelopes = tracked.Executed.Envelopes()
            .Where(x => x.Message is CascadedMessage)
            .ToArray();

        cascadedEnvelopes.Length.ShouldBe(2);

        // Verify the saga ID was propagated to both cascading message envelopes
        foreach (var envelope in cascadedEnvelopes)
        {
            envelope.SagaId.ShouldBe(id.ToString());
        }

        // Verify the saga handled the cascaded message
        CascadingTestSaga.CascadeHandledCount.ShouldBe(1);

        // Verify the external handler also handled it
        ExternalHandler.CascadedMessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task cascading_message_from_start_method_should_have_saga_id()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CascadingFromStartSaga));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            }).StartAsync();

        var id = Guid.NewGuid();
        CascadingFromStartSaga.CascadeHandledCount = 0;

        // Start the saga - the Start method returns a cascading message
        var tracked = await host.SendMessageAndWaitAsync(new StartWithCascade(id));

        // Verify the cascading message was executed
        var cascadedEnvelopes = tracked.Executed.Envelopes()
            .Where(x => x.Message is CascadedFromStart)
            .ToArray();

        cascadedEnvelopes.Length.ShouldBe(1);

        // Verify the saga ID was propagated
        var cascadedEnvelope = cascadedEnvelopes.Single();
        cascadedEnvelope.SagaId.ShouldBe(id.ToString());

        // Verify the saga handled the cascaded message
        CascadingFromStartSaga.CascadeHandledCount.ShouldBe(1);
    }
}

public record StartCascadingTest(Guid Id);
public record TriggerCascade(Guid Id);
public record CascadedMessage;

public class CascadingTestSaga : Saga
{
    public Guid Id { get; set; }
    public static int CascadeHandledCount { get; set; }

    public static CascadingTestSaga Start(StartCascadingTest cmd)
    {
        CascadeHandledCount = 0;
        return new CascadingTestSaga { Id = cmd.Id };
    }

    public CascadedMessage Handle(TriggerCascade cmd)
    {
        Debug.WriteLine($"TriggerCascade handled by saga {Id}");
        return new CascadedMessage();
    }

    public void Handle(CascadedMessage msg)
    {
        Debug.WriteLine($"CascadedMessage handled by saga {Id}");
        CascadeHandledCount++;
    }
}

public record StartWithCascade(Guid Id);
public record CascadedFromStart;

public class CascadingFromStartSaga : Saga
{

    public Guid Id { get; set; }
    public static int CascadeHandledCount { get; set; }

    public static (CascadingFromStartSaga, CascadedFromStart) Start(StartWithCascade cmd)
    {
        return (new CascadingFromStartSaga { Id = cmd.Id }, new CascadedFromStart());
    }

    public void Handle(CascadedFromStart msg)
    {
        Debug.WriteLine($"CascadedFromStart handled by saga {Id}");
        CascadeHandledCount++;
    }
}


public static class ExternalHandler
{
    public static int CascadedMessageCount { get; set; }

    public static void Handle(CascadedMessage message)
    {
        Debug.WriteLine($"ExternalHandler.Handle handled {nameof(CascadedMessage)}");
        CascadedMessageCount++;
    }
}
