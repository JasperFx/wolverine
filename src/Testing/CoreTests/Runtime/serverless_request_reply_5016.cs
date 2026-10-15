using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Runtime;

public record ServerlessRequest(Guid Id);

public record ServerlessResponse(Guid Id);

/// <summary>
/// GH-5016. The serverless modes start no listeners, so a brokered reply never arrives. InvokeAsync&lt;T&gt;() used
/// to send the request, set off the remote handler, and then wait out the whole timeout
/// </summary>
public class serverless_request_reply_5016
{
    [Theory]
    [InlineData(DurabilityMode.Serverless)]
    [InlineData(DurabilityMode.ServerlessWithOutbox)]
    public async Task brokered_request_reply_is_refused_up_front(DurabilityMode mode)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<ServerlessRequest>().To("stub://serverless-requests");
            }).StartAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.MessageBus().InvokeAsync<ServerlessResponse>(new ServerlessRequest(Guid.NewGuid()),
                TestContext.Current.CancellationToken, 30.Seconds()).WaitAsync(10.Seconds()));

        ex.Message.ShouldContain("runs no listeners");
    }
}
