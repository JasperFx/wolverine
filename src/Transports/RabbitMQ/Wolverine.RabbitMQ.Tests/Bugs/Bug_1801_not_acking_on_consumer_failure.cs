using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_1801_not_acking_on_consumer_failure
{
    [Fact]
    public async Task try_it()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision();

                opts.ListenToRabbitQueue("will_error");
                opts.PublishMessage<CauseError>().ToRabbitQueue("will_error");
            }).StartAsync();

        var tracked = await host.TrackActivity()
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(1.Minutes())
            .SendMessageAndWaitAsync(new CauseError("Bang!"));
    }
}

public record CauseError(string Message);

public static class CauseErrorHandler
{
    public static void Handle(CauseError msg)
    {
        throw new InvalidOperationException(msg.Message);
    }
}