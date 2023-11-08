using System.Text.Json;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.AdminApi;
using Wolverine.EntityFrameworkCore;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Marten;
using WolverineWebApi;
using WolverineWebApi.Marten;
using WolverineWebApi.Samples;
using WolverineWebApi.WebSockets;
using Order = WolverineWebApi.Order;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddLogging();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSignalR();
builder.Services.AddSingleton<Broadcaster>();

builder.Services.AddAuthorization();

builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
    x => x.UseNpgsql(Servers.PostgresConnectionString));


builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "http";
}).IntegrateWithWolverine();


builder.Services.AddResourceSetupOnStartup();

builder.Services.AddSingleton<Recorder>();

// Need this.
builder.Host.UseWolverine(opts =>
{
    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    opts.Policies.AutoApplyTransactions();

    opts.UseFluentValidation();
    opts.Discovery.IncludeAssembly(typeof(CreateCustomer2).Assembly);
    
    opts.OptimizeArtifactWorkflow();
    
    opts.Policies.Add<BroadcastClientMessages>();
    
});

builder.Services.ConfigureSystemTextJsonForWolverineOrMinimalApi(o =>
{
    // Do whatever you want here to customize the JSON
    // serialization
    o.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

//Force the default culture to not be en-US to ensure code is culture agnostic
var supportedCultures = new[] { "fr-FR", "en-US" };
var localizationOptions = new RequestLocalizationOptions().SetDefaultCulture(supportedCultures[0])
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.MapGet("/orders/{orderId}", [Authorize] Results<BadRequest, Ok<Order>>(int orderId)
    => orderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new Order(orderId)));

app.MapPost("/orders", Results<BadRequest, Ok<Order>>(CreateOrder command)
    => command.OrderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new Order(command.OrderId)));

app.MapHub<BroadcastHub>("/updates");

app.MapWolverineAdminApiEndpoints();

#region sample_using_configure_endpoints

app.MapWolverineEndpoints(opts =>
{
    // This is strictly to test the endpoint policy

    opts.ConfigureEndpoints(httpChain =>
    {
        // The HttpChain model is a configuration time
        // model of how the HTTP endpoint handles requests

        // This adds metadata for OpenAPI
        httpChain.WithMetadata(new CustomMetadata());
        httpChain.WithTags("wolverine");
    });
    
    // more configuration for HTTP...
    
    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();

    #endregion
    
    // Only want this middleware on endpoints on this one handler
    opts.AddMiddleware(typeof(BeforeAndAfterMiddleware),
        chain => chain.Method.HandlerType == typeof(MiddlewareEndpoints));

#region sample_register_http_middleware_by_type
    opts.AddMiddlewareByMessageType(typeof(FakeAuthenticationMiddleware));
#endregion

    // Publish messages coming from 
    opts.PublishMessage<HttpMessage1>(HttpMethod.Post, "/publish/message1");
    opts.PublishMessage<HttpMessage2>("/publish/message2");
    opts.SendMessage<HttpMessage5>(HttpMethod.Post, "/send/message5");
    opts.SendMessage<HttpMessage6>("/send/message6");
    
    opts.AddPolicy<StreamCollisionExceptionPolicy>();


    #region sample_adding_custom_parameter_handling

    // Customizing parameter handling
    opts.AddParameterHandlingStrategy<NowParameterStrategy>();

    #endregion
});


#region sample_optimized_mediator_usage

// Functional equivalent to MapPost(pattern, (command, IMessageBus) => bus.Invoke(command))
app.MapPostToWolverine<HttpMessage1>("/wolverine");
app.MapPutToWolverine<HttpMessage2>("/wolverine");
app.MapDeleteToWolverine<HttpMessage3>("/wolverine");

// Functional equivalent to MapPost(pattern, (command, IMessageBus) => bus.Invoke<IResponse>(command))
app.MapPostToWolverine<CustomRequest, CustomResponse>("/wolverine/request");
app.MapDeleteToWolverine<CustomRequest, CustomResponse>("/wolverine/request");
app.MapPutToWolverine<CustomRequest, CustomResponse>("/wolverine/request");

    #endregion

await app.RunOaktonCommands(args);