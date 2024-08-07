using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using TestingSupport.Compliance;

namespace Wolverine.RabbitMQ.Tests;

public class Samples
{
    public static async Task listen_to_topics()
    {
        #region sample_publishing_to_rabbit_mq_topics_exchange

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();

                opts.Publish(x =>
                {
                    x.MessagesFromNamespace("SomeNamespace");
                    x.ToRabbitTopics("topics-exchange", ex =>
                    {
                        // optionally configure the exchange
                    });
                });

                opts.ListenToRabbitQueue("");
            }).StartAsync();

        #endregion

        #region sample_sending_topic_routed_message

        var publisher = host.MessageBus();
        await publisher.SendAsync(new Message1());

        #endregion

        #region sample_sending_to_a_specific_topic

        await publisher.BroadcastToTopicAsync("color.*", new Message1());

        #endregion
    }

    public static async Task disable_system_queue()
    {
        #region sample_disable_rabbit_mq_system_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // *A* way to configure Rabbit MQ using their Uri schema
                // documented here: https://www.rabbitmq.com/uri-spec.html
                opts.UseRabbitMq(new Uri("amqp://localhost"))

                    // Stop Wolverine from trying to create a reply queue
                    // for this node if your process does not have permission to
                    // do so against your Rabbit MQ broker
                    .DisableSystemRequestReplyQueueDeclaration();

                // Set up a listener for a queue, but also
                // fine-tune the queue characteristics if Wolverine
                // will be governing the queue setup
                opts.ListenToRabbitQueue("incoming2", q =>
                {
                    q.PurgeOnStartup = true;
                    q.TimeToLive(5.Minutes());
                });
            }).StartAsync();

        #endregion
    }

    public static async Task use_rabbit_for_control_queues()
    {
        #region sample_using_rabbit_mq_control_queues

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // *A* way to configure Rabbit MQ using their Uri schema
                // documented here: https://www.rabbitmq.com/uri-spec.html
                opts.UseRabbitMq(new Uri("amqp://localhost"))

                    // Use Rabbit MQ for inter-node communication
                    .EnableWolverineControlQueues();
            }).StartAsync();

        #endregion
    }

    public static async Task use_listener_connection_only()
    {
        #region sample_only_use_listener_connection_with_rabbitmq

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // *A* way to configure Rabbit MQ using their Uri schema
                // documented here: https://www.rabbitmq.com/uri-spec.html
                opts.UseRabbitMq(new Uri("amqp://localhost"))

                    // Turn on listener connection only in case if you only need to listen for messages
                    // The sender connection won't be activated in this case
                    .UseListenerConnectionOnly();

                // Set up a listener for a queue, but also
                // fine-tune the queue characteristics if Wolverine
                // will be governing the queue setup
                opts.ListenToRabbitQueue("incoming2", q =>
                {
                    q.PurgeOnStartup = true;
                    q.TimeToLive(5.Minutes());
                });
            }).StartAsync();

        #endregion
    }

    public static async Task use_sender_connection_only()
    {
        #region sample_only_use_sending_connection_with_rabbitmq

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // *A* way to configure Rabbit MQ using their Uri schema
                // documented here: https://www.rabbitmq.com/uri-spec.html
                opts.UseRabbitMq(new Uri("amqp://localhost"))

                    // Turn on sender connection only in case if you only need to send messages
                    // The listener connection won't be created in this case
                    .UseSenderConnectionOnly();

                // Set up a listener for a queue, but also
                // fine-tune the queue characteristics if Wolverine
                // will be governing the queue setup
                opts.ListenToRabbitQueue("incoming2", q =>
                {
                    q.PurgeOnStartup = true;
                    q.TimeToLive(5.Minutes());
                });
            }).StartAsync();

        #endregion
    }

    public static async Task listen_to_queue()
    {
        #region sample_listening_to_rabbitmq_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // *A* way to configure Rabbit MQ using their Uri schema
                // documented here: https://www.rabbitmq.com/uri-spec.html
                opts.UseRabbitMq(new Uri("amqp://localhost"));

                // Set up a listener for a queue
                opts.ListenToRabbitQueue("incoming1")
                    .PreFetchCount(100)
                    .ListenerCount(5) // use 5 parallel listeners
                    .CircuitBreaker(cb =>
                    {
                        cb.PauseTime = 1.Minutes();
                        // 10% failures will cause the listener to pause
                        cb.FailurePercentageThreshold = 10;
                    })
                    .UseDurableInbox();

                // Set up a listener for a queue, but also
                // fine-tune the queue characteristics if Wolverine
                // will be governing the queue setup
                opts.ListenToRabbitQueue("incoming2", q =>
                {
                    q.PurgeOnStartup = true;
                    q.TimeToLive(5.Minutes());
                });
            }).StartAsync();

        #endregion
    }

    public static async Task publish_to_queue()
    {
        #region sample_publish_to_rabbitmq_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Connect to an unsecured, local Rabbit MQ broker
                // at port 5672
                opts.UseRabbitMq();

                opts.PublishAllMessages().ToRabbitQueue("outgoing")
                    .UseDurableOutbox();

                // fine-tune the queue characteristics if Wolverine
                // will be governing the queue setup
                opts.PublishAllMessages().ToRabbitQueue("special", queue => { queue.IsExclusive = true; });
            }).StartAsync();

        #endregion
    }

    public static async Task publish_to_exchange()
    {
        #region sample_publish_to_rabbitmq_exchange

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Connect to an unsecured, local Rabbit MQ broker
                // at port 5672
                opts.UseRabbitMq();

                opts.PublishAllMessages().ToRabbitExchange("exchange1");

                // fine-tune the exchange characteristics if Wolverine
                // will be governing the queue setup
                opts.PublishAllMessages().ToRabbitExchange("exchange2", e =>
                {
                    // Default is Fanout, so overriding that here
                    e.ExchangeType = ExchangeType.Direct;

                    // If you want, you can also create binding here too
                    e.BindQueue("queue1", "exchange2ToQueue1");
                });
            }).StartAsync();

        #endregion
    }

    public static async Task publish_to_routing_key()
    {
        #region sample_publish_to_rabbitmq_routing_key

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq(rabbit => { rabbit.HostName = "localhost"; })
                    // I'm declaring an exchange, a queue, and the binding
                    // key that we're referencing below.
                    // This is NOT MANDATORY, but rather just allows Wolverine to
                    // control the Rabbit MQ object lifecycle
                    .DeclareExchange("exchange1", ex => { ex.BindQueue("queue1", "key1"); })

                    // This will direct Wolverine to create any missing Rabbit MQ exchanges,
                    // queues, or binding keys declared in the application at application
                    // start up time
                    .AutoProvision();

                opts.PublishAllMessages().ToRabbitExchange("exchange1");
            }).StartAsync();

        #endregion
    }

    public static async Task autopurge()
    {
        #region sample_autopurge_rabbitmq

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public static async Task autopurge_one_queue()
    {
        #region sample_autopurge_selective_queues

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .DeclareQueue("queue1")
                    .DeclareQueue("queue2", q => q.PurgeOnStartup = true);
            }).StartAsync();

        #endregion
    }

    public static async Task out_of_the_box_conventions()
    {
        #region sample_activating_rabbit_mq_conventional_routing

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    // Opt into conventional Rabbit MQ routing
                    .UseConventionalRouting();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_conventions()
    {
        #region sample_activating_rabbit_mq_conventional_routing_customized

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    // Opt into conventional Rabbit MQ routing
                    .UseConventionalRouting(x =>
                    {
                        // Customize the naming convention for the outgoing exchanges
                        x.ExchangeNameForSending(type => type.Name + "Exchange");

                        // Customize the naming convention for incoming queues
                        x.QueueNameForListener(type => type.FullName.Replace('.', '-'));

                        // Or maybe you want to conditionally configure listening endpoints
                        x.ConfigureListeners((listener, context) =>
                            {
                                if (context.MessageType.IsInNamespace("MyApp.Messages.Important"))
                                {
                                    listener.UseDurableInbox().ListenerCount(5);
                                }
                                else
                                {
                                    // If not important, let's make the queue be
                                    // volatile and purge older messages automatically
                                    listener.TimeToLive(2.Minutes());
                                }
                            })
                            // Or maybe you want to conditionally configure the outgoing exchange
                            .ConfigureSending((ex, _) => { ex.ExchangeType(ExchangeType.Direct); });
                    });
            }).StartAsync();

        #endregion
    }

    public static async Task customize_dead_letter_queueing()
    {
        #region sample_overriding_rabbit_mq_dead_letter_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Use a different default deal letter queue name
                opts.UseRabbitMq()
                    .CustomizeDeadLetterQueueing(new DeadLetterQueue("error-queue"))

                    // or conventionally
                    .ConfigureListeners(l => { l.DeadLetterQueueing(new DeadLetterQueue($"{l.QueueName}-errors")); });


                // Use a different dead letter queue for this specific queue
                opts.ListenToRabbitQueue("incoming")
                    .DeadLetterQueueing(new DeadLetterQueue("incoming-errors"));
            }).StartAsync();

        #endregion
    }

    public static async Task customize_dead_letter_queueing_to_interop()
    {
        #region sample_overriding_rabbit_mq_dead_letter_queue_interop_friendly

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Use a different default deal letter queue name
                opts.UseRabbitMq()
                    .CustomizeDeadLetterQueueing(
                        new DeadLetterQueue("error-queue", DeadLetterQueueMode.InteropFriendly))

                    // or conventionally
                    .ConfigureListeners(l =>
                    {
                        l.DeadLetterQueueing(new DeadLetterQueue($"{l.QueueName}-errors",
                            DeadLetterQueueMode.InteropFriendly));
                    });


                // Use a different dead letter queue for this specific queue
                opts.ListenToRabbitQueue("incoming")
                    .DeadLetterQueueing(new DeadLetterQueue("incoming-errors", DeadLetterQueueMode.InteropFriendly));
            }).StartAsync();

        #endregion
    }

    public static async Task disable_dead_letter_queueing_to_wolverine()
    {
        #region sample_disable_rabbit_mq_dead_letter_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Disable dead letter queueing by default
                opts.UseRabbitMq()
                    .DisableDeadLetterQueueing()

                    // or conventionally
                    .ConfigureListeners(l =>
                    {
                        // Really does the same thing as the first usage
                        l.DisableDeadLetterQueueing();
                    });


                // Disable the dead letter queue for this specific queue
                opts.ListenToRabbitQueue("incoming").DisableDeadLetterQueueing();
            }).StartAsync();

        #endregion
    }

    public static async Task configuring_to_receive_json_from_external_systems()
    {
        #region sample_setting_default_message_type_with_rabbit

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbit");

            opts.UseRabbitMq(rabbitMqConnectionString);

            opts.ListenToRabbitQueue("emails")
                // Tell Wolverine to assume that all messages
                // received at this queue are the SendEmail
                // message type
                .DefaultIncomingMessage<SendEmail>();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public static async Task configuring_custom_interop()
    {
        #region sample_registering_custom_rabbit_mq_envelope_mapper

        var builder = Host.CreateApplicationBuilder();

        builder.UseWolverine(opts =>
        {
            var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbit");

            opts.UseRabbitMq(rabbitMqConnectionString);

            opts.ListenToRabbitQueue("emails")
                // Apply your custom interoperability strategy here
                .UseInterop(new SpecialMapper())

                // You may still want to define the default incoming
                // message as the message type name may not be sent
                // by the upstream system
                .DefaultIncomingMessage<SendEmail>();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public static async Task publish_by_topic_rules()
    {
        #region sample_rabbit_topic_rules

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UseRabbitMq();

            // Publish any message that implements ITenantMessage to
            // a Rabbit MQ "Topic" exchange named "tenant.messages"
            opts.PublishMessagesToRabbitMqExchange<ITenantMessage>("tenant.messages",
                    m => $"{m.GetType().Name.ToLower()}/{m.TenantId}")

                // Specify or configure sending through Wolverine for all
                // messages through this Exchange
                .BufferedInMemory();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    #region sample_RabbitMQ_configuration_in_wolverine_extension

    public class MyModuleExtension : IWolverineExtension
    {
        public void Configure(WolverineOptions options)
        {
            options.ConfigureRabbitMq()
                // Make any Rabbit Mq configuration or declare
                // additional Rabbit Mq options through the normal
                // syntax
                .DeclareExchange("my-module")
                .DeclareQueue("my-queue");
        }
    }

    #endregion
}

#region sample_rabbit_itenantmessage

public interface ITenantMessage
{
    string TenantId { get; }
}

#endregion

public record SendEmail;

public static class AdditionalBrokers
{
    public static async Task configure()
    {
        #region sample_configure_additional_rabbit_mq_broker

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // Connect to the "main" Rabbit MQ broker for this application
            opts.UseRabbitMq(builder.Configuration.GetConnectionString("internal-rabbit-mq"));

            // Listen for incoming messages on the main broker at the queue named "incoming"
            opts.ListenToRabbitQueue("incoming");

            // Let's say there's one Rabbit MQ broker for internal communications
            // and a second one for external communications
            var external = new BrokerName("external");

            // BUT! Let's also use a second broker
            opts.AddNamedRabbitMqBroker(external, factory =>
            {
                factory.Uri = new Uri(builder.Configuration.GetConnectionString("external-rabbit-mq"));
            });

            // Listen to a queue on the named, secondary broker
            opts.ListenToRabbitQueueOnNamedBroker(external, "incoming");
            
            // Other options for publishing messages to the named broker
            opts.PublishAllMessages().ToRabbitExchangeOnNamedBroker(external, "exchange1");

            opts.PublishAllMessages().ToRabbitQueueOnNamedBroker(external, "outgoing");

            opts.PublishAllMessages().ToRabbitRoutingKeyOnNamedBroker(external, "exchange1", "key2");

            opts.PublishAllMessages().ToRabbitTopicsOnNamedBroker(external, "topics");
        });

        #endregion

        var host = builder.Build();
        await host.StartAsync();
    }
}