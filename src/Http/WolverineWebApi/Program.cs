using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.AdminApi;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Http.Marten;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Http.Transport;
using Wolverine.Marten;
using WolverineWebApi;
using WolverineWebApi.Marten;
using WolverineWebApi.Samples;
using WolverineWebApi.WebSockets;

#region sample_adding_http_services

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Necessary services for Wolverine HTTP
// And don't worry, if you forget this, Wolverine
// will assert this is missing on startup:(
builder.Services.AddWolverineHttp();

#endregion


builder.Services.AddLogging();
builder.Services.AddEndpointsApiExplorer();

#region sample_register_custom_swashbuckle_filter

builder.Services.AddSwaggerGen(x =>
{
    x.OperationFilter<WolverineOperationFilter>();
});

#endregion

builder.Services.AddSignalR();
builder.Services.AddSingleton<Broadcaster>();

builder.Services.AddAuthorization();

builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
    x => x.UseNpgsql(Servers.PostgresConnectionString));


builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "http";
    opts.DisableNpgsqlLogging = true;

    // Use this setting to get the very best performance out
    // of the UpdatedAggregate workflow and aggregate handler
    // workflow over all
    opts.Events.UseIdentityMapForAggregates = true;
}).IntegrateWithWolverine();



builder.Services.AddResourceSetupOnStartup();

builder.Services.AddSingleton<Recorder>();

// Need this.
builder.Host.UseWolverine(opts =>
{
    // I'm speeding this up a lot for faster tests
    opts.Durability.ScheduledJobPollingTime = 250.Milliseconds(); 
    
    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();

    opts.Durability.Mode = DurabilityMode.Solo;

    // Other Wolverine configuration...
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableLocalQueues();
    
    opts.Policies.OnExceptionOfType(typeof(AlwaysDeadLetterException)).MoveToErrorQueue();

    opts.UseFluentValidation();
    opts.Discovery.IncludeAssembly(typeof(CreateCustomer2).Assembly);

    opts.OptimizeArtifactWorkflow();
    
    opts.Policies.Add<BroadcastClientMessages>();

    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
});

builder.Services.ConfigureSystemTextJsonForWolverineOrMinimalApi(o =>
{
    // Do whatever you want here to customize the JSON
    // serialization
    o.SerializerOptions.WriteIndented = true;
});

#region sample_calling_ApplyAsyncWolverineExtensions

var app = builder.Build();

// In order for async Wolverine extensions to apply to Wolverine.HTTP configuration,
// you will need to explicitly call this *before* MapWolverineEndpoints()
await app.Services.ApplyAsyncWolverineExtensions();

#endregion

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

// These routes are for doing
OpenApiEndpoints.BuildComparisonRoutes(app);


app.MapGet("/orders/{orderId}", [Authorize] Results<BadRequest, Ok<TinyOrder>>(int orderId)
    => orderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new TinyOrder(orderId)));

app.MapPost("/orders", Results<BadRequest, Ok<TinyOrder>>(CreateOrder command)
    => command.OrderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new TinyOrder(command.OrderId)));

app.MapHub<BroadcastHub>("/updates");

app.MapWolverineAdminApiEndpoints();

#region sample_register_dead_letter_endpoints
app.MapDeadLettersEndpoints()

    // It's a Minimal API endpoint group,
    // so you can add whatever authorization
    // or OpenAPI metadata configuration you need
    // for just these endpoints
    //.RequireAuthorization("Admin")

    ;
#endregion

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
    });

    // more configuration for HTTP...

    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();

    #endregion

    // Only want this middleware on endpoints on this one handler
    opts.AddMiddleware(typeof(BeforeAndAfterMiddleware),
        chain => chain.Method.HandlerType == typeof(MiddlewareEndpoints));

#region sample_user_marten_compiled_query_policy
    opts.UseMartenCompiledQueryResultPolicy();
#endregion

#region sample_register_http_middleware_by_type
    opts.AddMiddlewareByMessageType(typeof(FakeAuthenticationMiddleware));
    opts.AddMiddlewareByMessageType(typeof(CanShipOrderMiddleWare));
#endregion

#region sample_register_resource_writer_policy
    opts.AddResourceWriterPolicy<CustomResourceWriterPolicy>();
#endregion

    // Publish messages coming from
    opts.PublishMessage<HttpMessage1>(HttpMethod.Post, "/publish/message1").WithTags("messages");
    opts.PublishMessage<HttpMessage2>("/publish/message2").WithTags("messages");
    opts.SendMessage<HttpMessage5>(HttpMethod.Post, "/send/message5").WithTags("messages");
    opts.SendMessage<HttpMessage6>("/send/message6").WithTags("messages");
    opts.SendMessage<MessageThatAlwaysGoesToDeadLetter>(HttpMethod.Post, "/send/always-dead-letter").WithTags("messages");

    opts.AddPolicy<StreamCollisionExceptionPolicy>();


    #region sample_adding_custom_parameter_handling

    // Customizing parameter handling
    opts.AddParameterHandlingStrategy<NowParameterStrategy>();

    #endregion
});

// TODO -- consider making this an option within UseWolverine????
app.MapWolverineHttpTransportEndpoints();

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

await app.RunJasperFxCommands(args);