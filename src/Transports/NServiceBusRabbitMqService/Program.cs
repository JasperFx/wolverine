using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using NServiceBus;

namespace NServiceBusRabbitMqService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseNServiceBus(_ =>
                {
                    var endpointConfiguration = new EndpointConfiguration("nsb");
                    endpointConfiguration.UseSerialization<NServiceBus.NewtonsoftJsonSerializer>();

                    var transport = endpointConfiguration.UseTransport<RabbitMQTransport>();
                    
                    transport.UseConventionalRoutingTopology(QueueType.Quorum);
                    transport.ConnectionString("host=localhost");
                    
                    return endpointConfiguration;
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls("http://localhost:5675");
                    webBuilder.UseStartup<Startup>();
                });
    }
}