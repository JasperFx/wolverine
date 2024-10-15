using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;
using Wolverine.Attributes;

namespace DocumentationSamples
{
    #region sample_ValidMessageHandlers

    [WolverineHandler]
    public class ValidMessageHandlers
    {
        // There's only one argument, so we'll assume that
        // argument is the message
        public void Handle(Message1 something)
        {
        }

        // The parameter named "message" is assumed to be the message type
        public Task ConsumeAsync(Message1 message, IDocumentSession session)
        {
            return session.SaveChangesAsync();
        }

        // In this usage, we're "cascading" a new message of type
        // Message2
        public Task<Message2> HandleAsync(Message1 message, IDocumentSession session)
        {
            return Task.FromResult(new Message2());
        }

        // In this usage we're "cascading" 0 to many additional
        // messages from the return value
        public IEnumerable<object> Handle(Message3 message)
        {
            yield return new Message1();
            yield return new Message2();
        }

        // It's perfectly valid to have multiple handler methods
        // for a given message type. Each will be called in sequence
        // they were discovered
        public void Consume(Message1 input, IEmailService emails)
        {
        }

        // You can inject additional services directly into the handler
        // method
        public ValueTask ConsumeAsync(Message3 weirdName, IEmailService service)
        {
            return ValueTask.CompletedTask;
        }

        public interface IEvent
        {
            string CustomerId { get; }
            Guid Id { get; }
        }
    }

    #endregion

    public interface IProjectRepository
    {
        Task CreateAsync(Project project);
    }

    public record CreateProject(string Name);

    #region sample_instance_handler

    // Wolverine does constructor injection as you're probably
    // used to with basically every other framework in .NET
    public class CreateProjectHandler(IProjectRepository Repository)
    {
        public async Task HandleAsync(CreateProject message)
        {
            await Repository.CreateAsync(new Project(message.Name));
        }
    }

    #endregion

    public record Project(string Name);

    public interface IEmailService;


    #region sample_simplest_possible_handler

    public class MyMessageHandler
    {
        public void Handle(MyMessage message)
        {
            Console.WriteLine("I got a message!");
        }
    }

    #endregion

    public class CallingMyMessageHandler
    {
        #region sample_publish_MyMessage

        public static async Task publish_command(IMessageBus bus)
        {
            await bus.PublishAsync(new MyMessage());
        }

        #endregion
    }

    namespace One
    {
        #region sample_ExampleHandlerByInstance

        public class ExampleHandler
        {
            public void Handle(Message1 message)
            {
                // Do work synchronously
            }

            public Task Handle(Message2 message)
            {
                // Do work asynchronously
                return Task.CompletedTask;
            }
        }

        #endregion
    }

    namespace Two
    {
        #region sample_ExampleHandlerByStaticMethods

        public static class ExampleHandler
        {
            public static void Handle(Message1 message)
            {
                // Do work synchronously
            }

            public static Task Handle(Message2 message)
            {
                // Do work asynchronously
                return Task.CompletedTask;
            }
        }

        #endregion
    }

    namespace Sample2
    {
        [WolverineIgnore]

        #region sample_HandlerBuiltByConstructorInjection

        public class ServiceUsingHandler
        {
            private readonly IDocumentSession _session;

            public ServiceUsingHandler(IDocumentSession session)
            {
                _session = session;
            }

            public Task Handle(InvoiceCreated created)
            {
                var invoice = new Invoice { Id = created.InvoiceId };
                _session.Store(invoice);

                return _session.SaveChangesAsync();
            }
        }

        #endregion
    }

    namespace Three
    {
        [WolverineIgnore]

        #region sample_HandlerUsingMethodInjection

        public static class MethodInjectionHandler
        {
            public static Task Handle(InvoiceCreated message, IDocumentSession session)
            {
                var invoice = new Invoice { Id = message.InvoiceId };
                session.Store(invoice);

                return session.SaveChangesAsync();
            }
        }

        #endregion
    }

    #region sample_HandlerUsingEnvelope

    public class EnvelopeUsingHandler
    {
        public void Handle(InvoiceCreated message, Envelope envelope)
        {
            var howOldIsThisMessage =
                DateTimeOffset.Now.Subtract(envelope.SentAt);
        }
    }

    #endregion


    public class Invoice
    {
        public Guid Id { get; set; }
    }

    public static class HandlerExamples
    {
        public static async Task explicit_handler_discovery()
        {
            #region sample_ExplicitHandlerDiscovery

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // No automatic discovery of handlers
                    opts.DisableConventionalDiscovery();
                }).StartAsync();

            #endregion
        }
    }
}