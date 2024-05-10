using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.SqlServer;

namespace SqlServerTests.Transport;

public class DocumentationSamples
{
    public static async Task Bootstrapping()
    {
        #region sample_using_sql_server_transport

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                var connectionString = context.Configuration.GetConnectionString("sqlserver");
                opts.UseSqlServerPersistenceAndTransport(connectionString, "myapp")

                    // Tell Wolverine to build out all necessary queue or scheduled message
                    // tables on demand as needed
                    .AutoProvision()

                    // Optional that may be helpful in testing, but probably bad
                    // in production!
                    .AutoPurgeOnStartup();


                // Use this extension method to create subscriber rules
                opts.PublishAllMessages().ToSqlServerQueue("outbound");

                // Use this to set up queue listeners
                opts.ListenToSqlServerQueue("inbound")

                    .CircuitBreaker(cb =>
                    {
                        // fine tune the circuit breaker
                        // policies here
                    })

                    // Optionally specify how many messages to
                    // fetch into the listener at any one time
                    .MaximumMessagesToReceive(50);
            }).StartAsync();

        #endregion
    }
}