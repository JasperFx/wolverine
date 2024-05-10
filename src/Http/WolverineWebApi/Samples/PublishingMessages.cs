using Wolverine;
using Wolverine.Http;
using WolverineWebApi.Marten;

namespace WolverineWebApi.Samples;

public class PublishingMessages
{
    public void setup_for_publishing()
    {
        #region sample_publish_http_methods_directly_to_Wolverine

        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine();

        var app = builder.Build();

        app.MapWolverineEndpoints(opts =>
        {
            opts.PublishMessage<CreateOrder>("/orders/create", chain =>
            {
                // You can make any necessary metadata configurations exactly
                // as you would for Minimal API endpoints with this syntax
                // to fine tune OpenAPI generation or security
                chain.Metadata.RequireAuthorization();
            });
            opts.PublishMessage<ShipOrder>(HttpMethod.Put, "/orders/ship");
        });

        // and the rest of your application configuration and bootstrapping

        #endregion
    }
}