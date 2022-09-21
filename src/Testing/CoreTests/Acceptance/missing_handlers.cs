using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class missing_handlers
{
    [Fact]
    public async Task calls_all_the_missing_handlers()
    {
        using var host = WolverineHost.For(x =>
        {
            x.Services.AddSingleton<IMissingHandler, RecordingMissingHandler>();
            x.Services.AddSingleton<IMissingHandler, RecordingMissingHandler2>();
            x.Services.AddSingleton<IMissingHandler, RecordingMissingHandler3>();
        });

        var message = new MessageWithNoHandler();


        await host.ExecuteAndWaitValueTaskAsync(x => x.EnqueueAsync(message));

        for (var i = 0; i < 4; i++)
        {
            if (RecordingMissingHandler.Recorded.Any())
            {
                break;
            }

            await Task.Delay(250);
        }

        RecordingMissingHandler.Recorded.Single().Message.ShouldBeSameAs(message);
        RecordingMissingHandler2.Recorded.Single().Message.ShouldBeSameAs(message);
        RecordingMissingHandler3.Recorded.Single().Message.ShouldBeSameAs(message);
    }

    public class RecordingMissingHandler : IMissingHandler
    {
        public static IList<Envelope> Recorded = new List<Envelope>();

        public ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
        {
            Recorded.Add(context.Envelope);

            root.ShouldNotBeNull();

            return ValueTask.CompletedTask;
        }
    }

    public class RecordingMissingHandler2 : IMissingHandler
    {
        public static IList<Envelope> Recorded = new List<Envelope>();

        public ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
        {
            Recorded.Add(context.Envelope);

            root.ShouldNotBeNull();

            return ValueTask.CompletedTask;
        }
    }

    public class RecordingMissingHandler3 : IMissingHandler
    {
        public static IList<Envelope> Recorded = new List<Envelope>();

        public ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root)
        {
            Recorded.Add(context.Envelope);

            root.ShouldNotBeNull();

            return ValueTask.CompletedTask;
        }
    }

    public class MessageWithNoHandler
    {
    }
}
