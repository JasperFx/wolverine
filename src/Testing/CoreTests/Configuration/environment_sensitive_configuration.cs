using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

public class environment_sensitive_configuration
{
    [Fact]
    public void can_use_hosting_as_part_of_the_configuration()
    {
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                if (context.HostingEnvironment.IsDevelopment())
                {
                    opts.Services.AddSingleton(new RegisteredMarker { Name = "Kendall Fuller" });
                }

                if (context.HostingEnvironment.IsStaging())
                {
                    opts.Services.AddSingleton(new RegisteredMarker { Name = "Darrel Williams" });
                }
            })
            .UseEnvironment("Development");

        using (var host = builder.Build())
        {
            host.Services.GetRequiredService<RegisteredMarker>()
                .Name.ShouldBe("Kendall Fuller");
        }
    }
}

public class RegisteredMarker
{
    public string Name { get; set; }
}