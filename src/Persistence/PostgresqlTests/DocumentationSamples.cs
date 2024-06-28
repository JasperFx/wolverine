using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class DocumentationSamples
{
    public static async Task Bootstrapping()
    {
        #region sample_using_postgres_transport

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var connectionString = builder.Configuration.GetConnectionString("postgres");
            opts.UsePostgresqlPersistenceAndTransport(connectionString, "myapp")

                // Tell Wolverine to build out all necessary queue or scheduled message
                // tables on demand as needed
                .AutoProvision()

                // Optional that may be helpful in testing, but probably bad
                // in production!
                .AutoPurgeOnStartup();


            // Use this extension method to create subscriber rules
            opts.PublishAllMessages().ToPostgresqlQueue("outbound");

            // Use this to set up queue listeners
            opts.ListenToPostgresqlQueue("inbound")

                .CircuitBreaker(cb =>
                {
                    // fine tune the circuit breaker
                    // policies here
                })

                // Optionally specify how many messages to
                // fetch into the listener at any one time
                .MaximumMessagesToReceive(50);
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }
}