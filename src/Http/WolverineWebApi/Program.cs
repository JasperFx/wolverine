using System.Threading.RateLimiting;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Scalar.AspNetCore;
using Wolverine;
using Wolverine.AdminApi;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.ApiVersioning;
using Wolverine.Http.FluentValidation;
using Wolverine.Http.Marten;
using Wolverine.Http.Tests.DifferentAssembly.Validation;
using Wolverine.Http.Transport;
using Wolverine.Marten;
using WolverineWebApi;
using WolverineWebApi.ApiVersioning;
using WolverineWebApi.Bugs;
using WolverineWebApi.Marten;
using WolverineWebApi.Samples;
using WolverineWebApi.Things;
using WolverineWebApi.WebSockets;
using WolverineWebApiFSharp;

namespace WolverineWebApi;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
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
            x.SwaggerDoc("default", new OpenApiInfo { Title = "Wolverine Web API", Version = "default" });
            x.SwaggerDoc("v1", new OpenApiInfo { Title = "Wolverine Web API v1", Version = "v1" });
            x.SwaggerDoc("v2", new OpenApiInfo { Title = "Wolverine Web API v2", Version = "v2" });
            x.SwaggerDoc("v3", new OpenApiInfo { Title = "Wolverine Web API v3", Version = "v3" });
            // v4 has no options.Deprecate("4.0") — used by integration tests to prove the
            // attribute-driven [ApiVersion("4.0", Deprecated = true)] is honoured on its own.
            x.SwaggerDoc("v4", new OpenApiInfo { Title = "Wolverine Web API v4", Version = "v4" });
            x.OperationFilter<WolverineOperationFilter>();
            x.OperationFilter<WolverineApiVersioningSwaggerOperationFilter>();
            x.DocInclusionPredicate((docName, api) =>
                docName == "default" || api.GroupName == docName);
            x.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
        });

        #endregion

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<Broadcaster>();

        builder.Services.AddAuthorization();

        #region sample_adding_output_cache_services
        builder.Services.AddOutputCache(options =>
        {
            options.AddPolicy("short", builder => builder.Expire(TimeSpan.FromSeconds(5)));
        });

        #region sample_rate_limiting_configuration
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("fixed", opt =>
            {
                opt.PermitLimit = 1;
                opt.Window = TimeSpan.FromSeconds(10);
                opt.QueueLimit = 0;
            });
            options.RejectionStatusCode = 429;
        });
        #endregion

        #endregion

        builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Services.AddKeyedSingleton<IThing, RedThing>("Red");
        builder.Services.AddKeyedScoped<IThing, BlueThing>("Blue");
        builder.Services.AddKeyedTransient<IThing, GreenThing>("Green");

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

        builder.Services.AddMartenStore<IThingStore>(options =>
        {
            options.Connection(Servers.PostgresConnectionString);
            options.DatabaseSchemaName = "things";

            // Configure the event store to use strings as identifiers to support resource names.
            options.Events.StreamIdentity = StreamIdentity.AsString;

            // Add projections
            options.Projections.Add<ThingProjection>(ProjectionLifecycle.Inline);
        }).IntegrateWithWolverine();

        builder.Services.AddResourceSetupOnStartup();

        builder.Services.AddSingleton<Recorder>();

// Need this.
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.MessageStorageSchemaName = "wolverine";

            // I'm speeding this up a lot for faster tests
            opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

            // Set up Entity Framework Core as the support
            // for Wolverine's transactional middleware
            opts.UseEntityFrameworkCoreTransactions();

            opts.Durability.Mode = DurabilityMode.Solo;

            opts.EnableRelayOfUserName = true;

            // Other Wolverine configuration...
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();

            opts.Policies.OnExceptionOfType(typeof(AlwaysDeadLetterException)).MoveToErrorQueue();

            opts.UseFluentValidation();
            opts.Discovery.IncludeAssembly(typeof(CreateCustomer2).Assembly);
            opts.Discovery.IncludeAssembly(typeof(DiscoverFSharp).Assembly);

            opts.Services.CritterStackDefaults(x =>
            {
                x.Production.GeneratedCodeMode = TypeLoadMode.Static;
                x.Production.ResourceAutoCreate = AutoCreate.None;

                // These are defaults, but showing for completeness
                x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
                x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
            });

            opts.Policies.Add<BroadcastClientMessages>();
        });

// These settings would apply to *both* Marten and Wolverine
// if you happen to be using both
        builder.Services.CritterStackDefaults(x =>
        {
            x.ServiceName = "MyService";
            x.TenantIdStyle = TenantIdStyle.ForceLowerCase;

            // You probably won't have to configure this often,
            // but if you do, this applies to both tools
            x.ApplicationAssembly = typeof(Program).Assembly;

            x.Production.GeneratedCodeMode = TypeLoadMode.Static;
            x.Production.ResourceAutoCreate = AutoCreate.None;

            // These are defaults, but showing for completeness
            x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
            x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
        });

        builder.Services.ConfigureSystemTextJsonForWolverineOrMinimalApi(o =>
        {
            // Do whatever you want here to customize the JSON
            // serialization
            o.SerializerOptions.WriteIndented = true;
        });

        #region sample_calling_applyasyncwolverineextensions
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

        // Scoped UseExceptionHandler — only on the dedicated regression-test path. Pinning the
        // documented out-of-scope: 5xx responses produced by the global ASP.NET Core exception
        // handler bypass the chain pipeline and therefore must NOT carry versioning headers.
        // Restricted to /v1/orders/throws so other tests that intentionally produce 5xx via
        // Wolverine's own ProblemDetails OnException middleware are unaffected.
        // Must be registered before MapWolverineEndpoints so it wraps the chain pipeline.
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/v1/orders/throws"),
            branch => branch.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync("global-exception-handler");
            })));

// Configure the HTTP request pipeline.
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/default/swagger.json", "default");
            foreach (var description in app.DescribeWolverineApiVersions())
            {
                c.SwaggerEndpoint(
                    $"/swagger/{description.DocumentName}/swagger.json",
                    description.DisplayName + (description.IsDeprecated ? " (deprecated)" : ""));
            }
        });

        app.MapScalarApiReference(options =>
        {
            options.AddDocument("default", "Wolverine Web API (all)",
                $"/swagger/default/swagger.json");
            foreach (var description in app.DescribeWolverineApiVersions())
            {
                options.AddDocument(
                    description.DocumentName,
                    description.DisplayName + (description.IsDeprecated ? " (deprecated)" : ""),
                    $"/swagger/{description.DocumentName}/swagger.json");
            }
        });

        app.UseAuthorization();

        #region sample_using_output_cache_middleware
        app.UseOutputCache();

        #region sample_use_rate_limiter_middleware
        app.UseRateLimiter();
        #endregion

        #endregion

// These routes are for doing
        OpenApiEndpoints.BuildComparisonRoutes(app);


        app.MapGet("/orders/{orderId}", [Authorize] Results<BadRequest, Ok<TinyOrder>> (int orderId)
            => orderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new TinyOrder(orderId)));

        app.MapPost("/orders", Results<BadRequest, Ok<TinyOrder>> (CreateOrder command)
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
            opts.UseApiVersioning(v =>
            {
                // Existing unversioned endpoints are left unchanged
                v.UnversionedPolicy = UnversionedPolicy.PassThrough;
                v.Sunset("3.0").On(DateTimeOffset.Parse("2027-01-01T00:00:00Z"))
                    .WithLink(new Uri("https://example.com/migrate-to-v2"), "Migration guide", "text/html");
                v.Deprecate("1.0").On(DateTimeOffset.Parse("2026-12-31T00:00:00Z"))
                    .WithLink(new Uri("https://example.com/sunset-v1"));
            });

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

            // Or instead, you could use Data Annotations that are built
            // into the Wolverine.HTTP library
            opts.UseDataAnnotationsValidationProblemDetailMiddleware();

            #endregion

            // Only want this middleware on endpoints on this one handler
            opts.AddMiddleware(typeof(BeforeAndAfterMiddleware),
                chain => chain.Method.HandlerType == typeof(MiddlewareEndpoints));
            opts.AddMiddleware(typeof(LoadTodoMiddleware),
                chain => chain.Method.HandlerType == typeof(UpdateEndpointWithMiddleware));
            opts.AddPolicy<LoadTodoPolicy>();

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
            opts.SendMessage<MessageThatAlwaysGoesToDeadLetter>(HttpMethod.Post, "/send/always-dead-letter")
                .WithTags("messages");

            // Register OnException middleware for testing
            opts.AddMiddleware(typeof(GlobalExceptionMiddleware),
                chain => chain.Method.HandlerType == typeof(MiddlewareExceptionEndpoints));

            opts.AddPolicy<StreamCollisionExceptionPolicy>();

            opts.AddPolicy<FrameRearrangeMiddleware.HttpPolicy>();

            #region sample_adding_custom_parameter_handling
            // Customizing parameter handling
            opts.AddParameterHandlingStrategy<NowParameterStrategy>();

            #endregion
        });

        #region sample_mapwolverinehttptransportendpoints
        app.MapWolverineHttpTransportEndpoints();

        #endregion

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

        return await app.RunJasperFxCommands(args);
    }
}