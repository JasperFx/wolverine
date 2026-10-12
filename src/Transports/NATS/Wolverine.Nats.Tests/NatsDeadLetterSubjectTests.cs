using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Regression coverage for GH-3739: <c>NatsEndpoint.BuildListenerAsync</c> resolved the dead-letter sender by
/// casting the result of <c>IEndpointCollection.GetOrBuildSendingAgent(...)</c> straight to <c>ISender</c>. That
/// method returns an <c>ISendingAgent</c> — a <c>BufferedSendingAgent</c>, <c>DurableSendingAgent</c> or
/// <c>InlineSendingAgent</c> — and none of those implement <c>ISender</c>, so the cast threw
/// <c>InvalidCastException</c> during <c>WolverineRuntime.StartAsync</c> for *every* listener configured with a
/// dead-letter subject, regardless of the destination endpoint's sending mode. The whole host failed to start;
/// the only workaround was to drop the dead-letter subject and let poison messages be discarded.
///
/// The NATS suite had no dead-letter coverage at all, which is how this survived two releases.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsDeadLetterSubjectTests
{
    private readonly ITestOutputHelper _output;

    public NatsDeadLetterSubjectTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task listener_with_a_dead_letter_subject_starts_up()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var id = Guid.NewGuid().ToString("N");
        var stream = $"DLQSTART_{id}";
        var subject = $"dlqstart.{id}.incoming";
        var deadLetterSubject = $"dlqstart-errors.{id}";

        // Before the fix this threw InvalidCastException out of StartAsync
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"dlqstart.{id}.>"));

                opts.Policies.DisableConventionalLocalRouting();

                opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, $"dlqstart-consumer-{id}")
                    .ConfigureDeadLetterQueue(1, deadLetterSubject);
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // ...and the sending agent of the dead-letter subject still wraps a NatsSender, not the other way round.
        // The listener itself forwards through a JetStream sender of its own (NatsEndpoint.BuildDeadLetterSender),
        // so it knows the copy is stored before it terminates the original
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();
        var dlqEndpoint = transport.EndpointForSubject(deadLetterSubject);
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(dlqEndpoint.Uri);

        agent.ShouldNotBeAssignableTo<ISender>();
        agent.ShouldBeAssignableTo<SendingAgent>()
            .Sender.ShouldBeOfType<NatsSender>();
    }

    [Fact]
    public async Task poison_message_is_forwarded_to_the_configured_dead_letter_subject()
    {
        var natsUrl = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(natsUrl)) return;

        var id = Guid.NewGuid().ToString("N");
        var stream = $"DLQFORWARD_{id}";
        var subject = $"dlqforward.{id}.incoming";

        // In a stream of its own: the copy is a JetStream publish the stream has to confirm, which a raw core
        // subscriber still sees
        var deadLetterSubject = $"dlqforward-errors.{id}";

        PoisonMessageHandler.Reset();

        // Subscribe before the host starts so the forward can't be missed
        await using var dlqSubscription = await NatsTestHelpers.SubscribeRawAsync(natsUrl, deadLetterSubject);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .AutoProvision()
                    .DefineStream(stream, s => s.WithSubjects($"dlqforward.{id}.>"))
                    .DefineStream($"{stream}_ERRORS", s => s.WithSubjects(deadLetterSubject));

                opts.Policies.DisableConventionalLocalRouting();

                // MaxDeliveryAttempts of 1 means the very first failure is already terminal, so the test
                // doesn't have to wait out a JetStream redelivery cycle
                opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, $"dlqforward-consumer-{id}")
                    .ConfigureDeadLetterQueue(1, deadLetterSubject);

                opts.PublishMessage<PoisonMessage>().ToNatsSubject(subject).UseJetStream(stream);

                opts.Policies.OnException<PoisonPillException>().MoveToErrorQueue();
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await host.MessageBus().SendAsync(new PoisonMessage(id));

        await PoisonMessageHandler.WaitForAttemptAsync();

        var deadLettered = await dlqSubscription.ReadAsync(30.Seconds());
        deadLettered.ShouldNotBeNull();

        // The forwarded copy carries the original body plus the failure metadata NatsListener stamps on it
        Encoding.UTF8.GetString(deadLettered.Value.Data!).ShouldContain(id);
        deadLettered.Value.Headers.ShouldNotBeNull();
        deadLettered.Value.Headers!["x-dlq-original-subject"].ToString().ShouldBe(subject);
    }
}

public record PoisonMessage(string Id);

public class PoisonPillException : Exception
{
    public PoisonPillException(string message) : base(message)
    {
    }
}

[WolverineHandler]
public static class PoisonMessageHandler
{
    private static TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        _attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Handle(PoisonMessage message)
    {
        _attempted.TrySetResult();
        throw new PoisonPillException($"This message is poison: {message.Id}");
    }

    public static Task WaitForAttemptAsync()
    {
        return _attempted.Task.WaitAsync(30.Seconds());
    }
}
