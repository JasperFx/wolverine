using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_226_disambiguate_loggers : PostgresqlContext
{
    [Fact]
    public async Task should_find_handler()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddScoped<IDependencyRequiringLogger, DependencyRequiringLogger>();

                services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();
            })
            .UseWolverine(opts => { opts.Policies.Add<RequiringLoggerPolicy>(); })
            .StartAsync();

        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new StoreSomething(id));
    }

    public interface IDependencyRequiringLogger;

    public class DependencyRequiringLogger : IDependencyRequiringLogger
    {
        public DependencyRequiringLogger(ILogger<DependencyRequiringLogger> logger)
        {
        }
    }

    public class RequiringLoggerPolicy : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains)
            {
                var method = GetType().GetMethod(nameof(SamplePolicy))!;

                var methodCall = new MethodCall(GetType(), method);

                chain.Middleware.Add(methodCall);
            }
        }

        public static Task SamplePolicy(ILogger logger, IDependencyRequiringLogger dep)
        {
            return Task.CompletedTask;
        }
    }
}

public record StoreSomething(Guid Id);

public class StoreSomethingHandler
{
    public static void Handle(StoreSomething command)
    {
    }
}