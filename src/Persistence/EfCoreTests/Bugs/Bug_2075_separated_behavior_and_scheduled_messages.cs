using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace EfCoreTests.Bugs;

public class Bug_2075_separated_behavior_and_scheduled_messages(ITestOutputHelper Output)
{
    [Fact]
    public async Task MyBug()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(o =>
                {
                    o.UseNpgsql(Servers.PostgresConnectionString);
                });

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddDbContext<AppDbContext>(opt =>
                    opt.UseNpgsql(Servers.PostgresConnectionString)
                );

                opts.Policies.UseDurableLocalQueues();
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                GlobalErrorHandlingPolicy.Invoke(opts);
            })
            .StartAsync();
        
        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().WaitForMessageToBeReceivedAt<SayStuffy2>(host).Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new SayStuffy0());

        session.Executed.SingleMessage<SayStuffy2>().ShouldNotBeNull();

        var records = session.AllRecordsInOrder().ToArray();
        foreach (var envelopeRecord in records)
        {
            Output.WriteLine(envelopeRecord.ToString());
        }
    }

    public record SayStuffy0();
    public record SayStuffy2();

    public record SayStuffy1(string Text);


    public class BSayStuffyHandler
    {
        public SayStuffy1 Handle(SayStuffy0 _)
        {
            return new SayStuffy1("Hello world");
        }

        public void Handle(SayStuffy1 stuff) => Debug.WriteLine(stuff.Text);
    }

    public class ASayStuffyHandler
    {
        public SayStuffy2 Handle(SayStuffy1 stuff, Envelope envelope)
        {
            if (envelope.Attempts < 2)
            {
                throw new Exception("Bye world");
            }

            return new SayStuffy2();

        }

        public static void Handle(SayStuffy2 m) => Debug.WriteLine("Got SayStuffy2");
    }
}

public static class GlobalErrorHandlingPolicy
{
    public static void Invoke(WolverineOptions options)
    {
        options
            .Policies.OnException<Exception>()
            .ScheduleRetry(1.Seconds(), 1.Seconds(), 1.Seconds(), 1.Seconds())
            .Then.Discard();
    }
}