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

    #region sample_programmatic_one_off_openapi_metadata

    public static void Configure(HttpChain chain)
    {
        // This sample is from Wolverine itself on endpoints where all you do is forward
        // a request directly to a Wolverine messaging endpoint for later processing
        chain.Metadata.Add(builder =>
        {
            // Adding metadata
            builder.Metadata.Add(new WolverineProducesResponseTypeMetadata { StatusCode = 202, Type = null });
        });
        // This is run after all other metadata has been applied, even after the wolverine built-in metadata
        // So use this if you want to change or remove some metadata
        chain.Metadata.Finally(builder =>
        {
            builder.RemoveStatusCodeResponse(200);
        });
    }

    #endregion
}