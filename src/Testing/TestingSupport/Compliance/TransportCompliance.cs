using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using TestingSupport.ErrorHandling;
using TestMessages;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace TestingSupport.Compliance;

public abstract class TransportComplianceFixture : IDisposable
{
    public readonly TimeSpan DefaultTimeout = 5.Seconds();

    protected TransportComplianceFixture(Uri destination, int defaultTimeInSeconds = 5)
    {
        OutboundAddress = destination;
        DefaultTimeout = defaultTimeInSeconds.Seconds();
    }

    public IHost Sender { get; private set; }
    public IHost Receiver { get; private set; }
    public Uri OutboundAddress { get; protected set; }

    public bool AllLocally { get; set; }

    public void Dispose()
    {
        Sender?.Dispose();
        if (!ReferenceEquals(Sender, Receiver))
        {
            Receiver?.Dispose();
        }
    }


    protected Task TheOnlyAppIs(Action<WolverineOptions> configure)
    {
        AllLocally = true;

        Sender = WolverineHost.For(options =>
        {
            configure(options);
            configureReceiver(options);
            configureSender(options);
        });

        return Task.CompletedTask;
    }

    protected async Task SenderIs(Action<WolverineOptions> configure)
    {
        Sender = WolverineHost.For(opts =>
        {
            configure(opts);
            configureSender(opts);
        });
    }

    private void configureSender(WolverineOptions options)
    {
        options.Handlers
            .DisableConventionalDiscovery()
            .IncludeType<PongHandler>();

        options.AddSerializer(new GreenTextWriter());
        options.ServiceName = "SenderService";
        options.PublishAllMessages().To(OutboundAddress);

        options.Services.AddSingleton<IMessageSerializer, GreenTextWriter>();
        options.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
    }

    public async Task ReceiverIs(Action<WolverineOptions> configure)
    {
        Receiver = WolverineHost.For(opts =>
        {
            configure(opts);
            configureReceiver(opts);
        });
    }

    private static void configureReceiver(WolverineOptions options)
    {
        options.Handlers.Failures.MaximumAttempts = 3;
        options.Handlers
            .DisableConventionalDiscovery()
            .IncludeType<MessageConsumer>()
            .IncludeType<ExecutedMessageGuy>()
            .IncludeType<ColorHandler>()
            .IncludeType<ErrorCausingMessageHandler>()
            .IncludeType<BlueHandler>()
            .IncludeType<PingHandler>();

        options.AddSerializer(new BlueTextReader());

        options.Handlers.OnException<DivideByZeroException>()
            .MoveToErrorQueue();

        options.Handlers.OnException<DataMisalignedException>()
            .Requeue();

        options.Handlers.OnException<BadImageFormatException>()
            .ScheduleRetry(3.Seconds());


        options.Services.AddSingleton(new ColorHistory());

        options.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
    }

    public virtual void BeforeEach()
    {
    }
}

public abstract class TransportCompliance<T> : IAsyncLifetime where T : TransportComplianceFixture, new()
{
    protected readonly ErrorCausingMessage theMessage = new();
    private ITrackedSession _session;
    protected Uri theOutboundAddress;
    protected IHost theReceiver;
    protected IHost theSender;

    protected TransportCompliance()
    {
        Fixture = new T();
    }

    public T Fixture { get; }

    public async Task InitializeAsync()
    {
        if (Fixture is IAsyncLifetime lifetime)
        {
            await lifetime.InitializeAsync();
        }

        theSender = Fixture.Sender;
        theReceiver = Fixture.Receiver;
        theOutboundAddress = Fixture.OutboundAddress;

        await Fixture.Sender.ResetResourceState();

        if (Fixture.Receiver != null && !ReferenceEquals(Fixture.Sender, Fixture.Receiver))
        {
            await Fixture.Receiver.ResetResourceState();
        }

        Fixture.BeforeEach();
    }

    public Task DisposeAsync()
    {
        Fixture.SafeDispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void all_listeners_say_they_are_accepting_on_startup()
    {
        var runtime = (theReceiver ?? theSender).Get<IWolverineRuntime>();
        foreach (var listener in runtime.Endpoints.ActiveListeners())
            listener.Status.ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public virtual async Task can_apply_requeue_mechanics()
    {
        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .Timeout(15.Seconds())
            .ExecuteAndWaitAsync(c => c.EndpointFor(theOutboundAddress).SendAsync( new Message2()));

        session.FindSingleTrackedMessageOfType<Message2>(EventType.MessageSucceeded)
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task can_send_from_one_node_to_another_by_destination()
    {
        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.EndpointFor(theOutboundAddress).SendAsync( new Message1()));


        session.FindSingleTrackedMessageOfType<Message1>(EventType.MessageSucceeded)
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task can_stop_and_restart_listeners()
    {
        var receiving = theReceiver ?? theSender;
        var runtime = receiving.Get<IWolverineRuntime>();

        foreach (var listener in runtime.Endpoints.ActiveListeners()
                     .Where(x => x.Endpoint.Role == EndpointRole.Application))
        {
            await listener.StopAndDrainAsync();

            listener.Status.ShouldBe(ListeningStatus.Stopped);
        }

        foreach (var listener in runtime.Endpoints.ActiveListeners()
                     .Where(x => x.Endpoint.Role == EndpointRole.Application))
        {
            await listener.StartAsync();

            listener.Status.ShouldBe(ListeningStatus.Accepting);
        }

        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.EndpointFor(theOutboundAddress).SendAsync( new Message1()));


        session.FindSingleTrackedMessageOfType<Message1>(EventType.MessageSucceeded)
            .ShouldNotBeNull();
    }


    [Fact]
    public async Task can_stop_receiving_when_too_busy_and_restart_listeners()
    {
        var receiving = theReceiver ?? theSender;
        var runtime = receiving.Get<IWolverineRuntime>();

        foreach (var listener in runtime.Endpoints.ActiveListeners()
                     .Where(x => x.Endpoint.Role == EndpointRole.Application))
        {
            await listener.MarkAsTooBusyAndStopReceivingAsync();

            listener.Status.ShouldBe(ListeningStatus.TooBusy);
        }

        foreach (var listener in runtime.Endpoints.ActiveListeners()
                     .Where(x => x.Endpoint.Role == EndpointRole.Application))
        {
            await listener.StartAsync();

            listener.Status.ShouldBe(ListeningStatus.Accepting);
        }

        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.EndpointFor(theOutboundAddress).SendAsync( new Message1()));


        session.FindSingleTrackedMessageOfType<Message1>(EventType.MessageSucceeded)
            .ShouldNotBeNull();
    }


    [Fact]
    public async Task can_send_from_one_node_to_another_by_publishing_rule()
    {
        var message1 = new Message1();

        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message1);


        session.FindSingleTrackedMessageOfType<Message1>(EventType.MessageSucceeded)
            .Id.ShouldBe(message1.Id);
    }

    [Fact]
    public async Task can_send_and_wait()
    {
        var message1 = new Message1();

        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .Timeout(30.Seconds())
            .InvokeMessageAndWaitAsync(message1);

    }

    [Fact]
    public async Task can_request_reply()
    {
        var request = new Request { Name = "Nick Bolton" };

        var (session, response) = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .InvokeAndWaitAsync<Response>(request);

        response.Name.ShouldBe(request.Name);
    }

    [Fact]
    public async Task tags_the_envelope_with_the_source()
    {
        var session = await theSender.TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.EndpointFor(theOutboundAddress).SendAsync( new Message1()));


        var record = session.FindEnvelopesWithMessageType<Message1>(EventType.MessageSucceeded).Single();
        record
            .ShouldNotBeNull();

        record.Envelope.Source.ShouldBe(theSender.Get<WolverineOptions>().ServiceName);
    }

    [Fact]
    public async Task tracking_correlation_id_on_everything()
    {
        var id2 = string.Empty;
        Func<IMessageContext, Task> action = async context =>
        {
            id2 = context.CorrelationId;

            await context.SendAsync(new ExecutedMessage());
            await context.PublishAsync(new ExecutedMessage());
            //await context.ScheduleSend(new ExecutedMessage(), DateTimeOffset.Now.AddDays(5));
        };

        var session2 = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .Timeout(1.Minutes())
            .ExecuteAndWaitAsync(action);

        var envelopes = session2
            .AllRecordsInOrder(EventType.Sent)
            .Select(x => x.Envelope)
            .ToArray();


        foreach (var envelope in envelopes) envelope.CorrelationId.ShouldBe(id2);
    }

    [Fact]
    public async Task schedule_send()
    {
        var session = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .Timeout(15.Seconds())
            .WaitForMessageToBeReceivedAt<ColorChosen>(theReceiver ?? theSender)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new ColorChosen { Name = "Orange" }, 5.Seconds()));

        var message = session.FindSingleTrackedMessageOfType<ColorChosen>(EventType.MessageSucceeded);
        message.Name.ShouldBe("Orange");
    }


    protected void throwOnAttempt<T>(int attempt) where T : Exception, new()
    {
        theMessage.Errors.Add(attempt, new T());
    }

    protected async Task<EnvelopeRecord> afterProcessingIsComplete()
    {
        _session = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        return _session.AllRecordsInOrder().Where(x => x.Envelope.Message is ErrorCausingMessage).LastOrDefault(x =>
            x.EventType == EventType.MessageSucceeded || x.EventType == EventType.MovedToErrorQueue);
    }

    protected async Task shouldSucceedOnAttempt(int attempt)
    {
        var session = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .Timeout(15.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(theMessage);

        var record = session.AllRecordsInOrder().Where(x => x.Envelope.Message is ErrorCausingMessage).LastOrDefault(
            x =>
                x.EventType == EventType.MessageSucceeded || x.EventType == EventType.MovedToErrorQueue);

        if (record == null)
        {
            throw new Exception("No ending activity detected");
        }

        if (record.EventType == EventType.MessageSucceeded && record.AttemptNumber == attempt)
        {
            return;
        }

        var writer = new StringWriter();

        await writer.WriteLineAsync($"Actual ending was '{record.EventType}' on attempt {record.AttemptNumber}");
        foreach (var envelopeRecord in session.AllRecordsInOrder())
        {
            writer.WriteLine(envelopeRecord);
            if (envelopeRecord.Exception != null)
            {
                await writer.WriteLineAsync(envelopeRecord.Exception.Message);
            }
        }

        throw new Exception(writer.ToString());
    }

    protected async Task shouldMoveToErrorQueueOnAttempt(int attempt)
    {
        var session = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(theMessage);

        var record = session.AllRecordsInOrder().Where(x => x.Envelope.Message is ErrorCausingMessage).LastOrDefault(
            x =>
                x.EventType == EventType.MessageSucceeded || x.EventType == EventType.MovedToErrorQueue);

        if (record == null)
        {
            throw new Exception("No ending activity detected");
        }

        if (record.EventType == EventType.MovedToErrorQueue && record.AttemptNumber == attempt)
        {
            return;
        }

        var writer = new StringWriter();

        writer.WriteLine($"Actual ending was '{record.EventType}' on attempt {record.AttemptNumber}");
        foreach (var envelopeRecord in session.AllRecordsInOrder())
        {
            writer.WriteLine(envelopeRecord);
            if (envelopeRecord.Exception != null)
            {
                writer.WriteLine(envelopeRecord.Exception.Message);
            }
        }

        throw new Exception(writer.ToString());
    }


    [Fact]
    public virtual async Task will_move_to_dead_letter_queue_without_any_exception_match()
    {
        throwOnAttempt<InvalidOperationException>(1);
        throwOnAttempt<InvalidOperationException>(2);
        throwOnAttempt<InvalidOperationException>(3);

        await shouldMoveToErrorQueueOnAttempt(3);
    }

    [Fact]
    public virtual async Task will_move_to_dead_letter_queue_with_exception_match()
    {
        throwOnAttempt<DivideByZeroException>(1);
        throwOnAttempt<DivideByZeroException>(2);
        throwOnAttempt<DivideByZeroException>(3);

        await shouldMoveToErrorQueueOnAttempt(1);
    }


    [Fact]
    public virtual async Task will_requeue_and_increment_attempts()
    {
        throwOnAttempt<DataMisalignedException>(1);
        throwOnAttempt<DataMisalignedException>(2);

        await shouldSucceedOnAttempt(3);
    }

    [Fact]
    public async Task can_schedule_retry()
    {
        throwOnAttempt<BadImageFormatException>(1);

        await shouldSucceedOnAttempt(2);
    }


    [Fact]
    public async Task explicit_respond_to_sender()
    {
        var ping = new PingMessage();

        var session = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(ping);

        session.FindSingleTrackedMessageOfType<PongMessage>(EventType.MessageSucceeded)
            .Id.ShouldBe(ping.Id);
    }

    [Fact]
    public async Task requested_response()
    {
        var ping = new ImplicitPing();

        var session = await theSender
            .TrackActivity(Fixture.DefaultTimeout)
            .AlsoTrack(theReceiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(x => x.SendAsync(ping, DeliveryOptions.RequireResponse<ImplicitPong>()));

        session.FindSingleTrackedMessageOfType<ImplicitPong>(EventType.MessageSucceeded)
            .Id.ShouldBe(ping.Id);
    }

    [Fact] // This test isn't always the most consistent test
    public async Task send_green_as_text_and_receive_as_blue()
    {
        if (Fixture.AllLocally)
        {
            return; // this just doesn't apply when running all with local queues
        }

        var greenMessage = new GreenMessage { Name = "Magic Johnson" };

        var session = await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .ExecuteAndWaitAsync(c => c.SendAsync(greenMessage, new DeliveryOptions { ContentType = "text/plain" }));

        session.FindSingleTrackedMessageOfType<BlueMessage>()
            .Name.ShouldBe("Magic Johnson");
    }

    [Fact]
    public async Task send_green_that_gets_received_as_blue()
    {
        if (Fixture.AllLocally)
        {
            return; // this just doesn't apply when running all with local queues
        }

        var session = await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new GreenMessage { Name = "Kareem Abdul-Jabbar" }));


        session.FindSingleTrackedMessageOfType<BlueMessage>()
            .Name.ShouldBe("Kareem Abdul-Jabbar");
    }
}

#region sample_BlueTextReader

public class BlueTextReader : IMessageSerializer
{
    public string ContentType { get; } = "text/plain";

    public byte[] Write(Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return ReadFromData(envelope.Data);
    }

    public object? ReadFromData(byte[]? data)
    {
        var name = Encoding.UTF8.GetString(data);
        return new BlueMessage { Name = name };
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotImplementedException();
    }
}

#endregion

#region sample_GreenTextWriter

public class GreenTextWriter : IMessageSerializer
{
    public string? ContentType { get; } = "text/plain";

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        throw new NotImplementedException();
    }

    public object? ReadFromData(byte[]? data)
    {
        throw new NotImplementedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotImplementedException();
    }

    public byte[] Write(Envelope model)
    {
        if (model.Message is GreenMessage green)
        {
            return Encoding.UTF8.GetBytes(green.Name);
        }

        throw new NotSupportedException("This serializer only writes GreenMessage");
    }
}

#endregion