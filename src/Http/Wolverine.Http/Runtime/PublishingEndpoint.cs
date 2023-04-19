using Microsoft.AspNetCore.Http;
using Wolverine.Http.Resources;

namespace Wolverine.Http.Runtime;

public class PublishingEndpoint<T>
{
    public async ValueTask<string> PublishAsync(T message, IMessageContext bus, HttpResponse response)
    {
        await bus.PublishAsync(message);
        response.StatusCode = 202;
        return "Success.";
    }

    public static void Configure(HttpChain chain)
    {
        chain.Metadata.Add(builder =>
        {
            builder.Metadata.Add(new ProducesResponseTypeMetadata { StatusCode = 202, Type = null });
            builder.RemoveStatusCodeResponse(200);
        });
    }
}