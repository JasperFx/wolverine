using Wolverine;
using Wolverine.Transports.Tcp;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtelMessages;
using OtelWebApi;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    opts.ServiceName = "WebApi";
    opts.ApplicationAssembly = typeof(InitialCommandHandler).Assembly;

    opts.PublishMessage<TcpMessage1>().ToPort(MessagingConstants.Subscriber1Port);

    opts.ListenAtPort(MessagingConstants.WebApiPort);

    opts.UseRabbitMq()
        .DeclareQueue(MessagingConstants.Subscriber1Queue)
        .DeclareQueue(MessagingConstants.Subscriber2Queue)
        .DeclareExchange(MessagingConstants.OtelExchangeName, ex =>
        {
            ex.BindQueue(MessagingConstants.Subscriber1Queue);
            ex.BindQueue(MessagingConstants.Subscriber2Queue);
        })
        .AutoProvision().AutoPurgeOnStartup();

    opts.PublishMessage<RabbitMessage1>()
        .ToRabbitExchange(MessagingConstants.OtelExchangeName);

});

builder.Services.AddControllers();

#region sample_enabling_open_telemetry

// builder.Services is an IServiceCollection object
builder.Services.AddOpenTelemetryTracing(x =>
{
    x.SetResourceBuilder(ResourceBuilder
            .CreateDefault()
            .AddService("OtelWebApi")) // <-- sets service name

        .AddJaegerExporter()
        .AddAspNetCoreInstrumentation()

        // This is absolutely necessary to collect the Wolverine
        // open telemetry tracing information in your application
        .AddSource("Wolverine");
});

#endregion

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Doing this just to get JSON formatters in here
builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

app.MapGet("/", c =>
{
    c.Response.Redirect("/swagger");
    return Task.CompletedTask;
});

app.Run();
