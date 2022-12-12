using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Transports.Tcp;
using Uri = System.Uri;

namespace DocumentationSamples;

public class PublishingSamples
{
    public static async Task LocalQueuesApp()
    {
        #region sample_LocalQueuesApp

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Force a local queue to be
                // strictly first in, first out
                // with no more than a single
                // thread handling messages enqueued
                // here

                // Use this option if message ordering is
                // important
                opts.LocalQueue("one")
                    .Sequential();

                // Specify the maximum number of parallel threads
                opts.LocalQueue("two")
                    .MaximumParallelMessages(5);


                // Or just edit the ActionBlock options directly
                opts.LocalQueue("three")
                    .ConfigureExecution(options =>
                    {
                        options.MaxDegreeOfParallelism = 5;
                        options.BoundedCapacity = 1000;
                    });

                // And finally, this enrolls a queue into the persistent inbox
                // so that messages can happily be retained and processed
                // after the service is restarted
                opts.LocalQueue("four").UseDurableInbox();
            }).StartAsync();

        #endregion
    }

    public static async Task sending_to_endpoint_by_name()
    {
        #region sample_sending_to_endpoint_by_name

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToPort(5555)
                    .Named("One");

                opts.PublishAllMessages().ToPort(5555)
                    .Named("Two");
            }).StartAsync();

        var bus = host.Services
            .GetRequiredService<IMessageBus>();

        // Explicitly send a message to a named endpoint
        await bus.EndpointFor("One").SendAsync( new SomeMessage());
        
        // Or invoke remotely
        await bus.EndpointFor("One").InvokeAsync(new SomeMessage());
        
        // Or request/reply
        var answer = bus.EndpointFor("One")
            .InvokeAsync<Answer>(new Question());

        #endregion


        #region sample_accessing_endpoint_by_uri

        // Or access operations on a specific endpoint using a Uri
        await bus.EndpointFor(new Uri("rabbitmq://queue/rabbit-one"))
            .InvokeAsync(new SomeMessage());

        #endregion
    }

    public record Question;

    public record Answer;


    #region sample_IServiceBus.Invoke

    public Task Invoke(IMessageContext bus)
    {
        var @event = new InvoiceCreated
        {
            Time = DateTimeOffset.Now,
            Purchaser = "Guy Fieri",
            Amount = 112.34,
            Item = "Cookbook"
        };

        return bus.InvokeAsync(@event);
    }

    #endregion

    #region sample_IServiceBus.Enqueue

    public ValueTask Publish(IMessageContext bus)
    {
        var @event = new InvoiceCreated
        {
            Time = DateTimeOffset.Now,
            Purchaser = "Guy Fieri",
            Amount = 112.34,
            Item = "Cookbook"
        };

        return bus.PublishAsync(@event);
    }

    #endregion

    #region sample_IServiceBus.Enqueue_to_specific_worker_queue

    public ValueTask EnqueueToQueue(IMessageContext bus)
    {
        var @event = new InvoiceCreated
        {
            Time = DateTimeOffset.Now,
            Purchaser = "Guy Fieri",
            Amount = 112.34,
            Item = "Cookbook"
        };

        // Put this message in a local worker
        // queue named 'highpriority'
        return bus.EndpointFor("highpriority").SendAsync(@event);
    }

    #endregion

    #region sample_send_delayed_message

    public async Task SendScheduledMessage(IMessageContext bus, Guid invoiceId)
    {
        var message = new ValidateInvoiceIsNotLate
        {
            InvoiceId = invoiceId
        };

        // Schedule the message to be processed in a certain amount
        // of time
        await bus.ScheduleAsync(message, 30.Days());

        // Schedule the message to be processed at a certain time
        await bus.ScheduleAsync(message, DateTimeOffset.Now.AddDays(30));
    }

    #endregion

    #region sample_schedule_job_locally

    public async Task ScheduleLocally(IMessageContext bus, Guid invoiceId)
    {
        var message = new ValidateInvoiceIsNotLate
        {
            InvoiceId = invoiceId
        };

        // Schedule the message to be processed in a certain amount
        // of time
        await bus.ScheduleAsync(message, 30.Days());

        // Schedule the message to be processed at a certain time
        await bus.ScheduleAsync(message, DateTimeOffset.Now.AddDays(30));
    }

    #endregion

    #region sample_sending_message_with_servicebus

    public ValueTask SendMessage(IMessageContext bus)
    {
        // In this case, we're sending an "InvoiceCreated"
        // message
        var @event = new InvoiceCreated
        {
            Time = DateTimeOffset.Now,
            Purchaser = "Guy Fieri",
            Amount = 112.34,
            Item = "Cookbook"
        };

        return bus.SendAsync(@event);
    }

    #endregion


    #region sample_publishing_message_with_servicebus

    public ValueTask PublishMessage(IMessageContext bus)
    {
        // In this case, we're sending an "InvoiceCreated"
        // message
        var @event = new InvoiceCreated
        {
            Time = DateTimeOffset.Now,
            Purchaser = "Guy Fieri",
            Amount = 112.34,
            Item = "Cookbook"
        };

        return bus.PublishAsync(@event);
    }

    #endregion

    public class InvoiceCreated
    {
        public DateTimeOffset Time { get; set; }
        public string Purchaser { get; set; }
        public double Amount { get; set; }
        public string Item { get; set; }
    }

    public class SomeMessage
    {
    }


    #region sample_send_message_to_specific_destination

    public async Task SendMessageToSpecificDestination(IMessageContext bus)
    {
        var @event = new InvoiceCreated
        {
            Time = DateTimeOffset.Now,
            Purchaser = "Guy Fieri",
            Amount = 112.34,
            Item = "Cookbook"
        };

        await bus.EndpointFor(new Uri("tcp://server1:2222")).SendAsync( @event);
    }


    public class ValidateInvoiceIsNotLate
    {
        public Guid InvoiceId { get; set; }
    }

    #endregion
}