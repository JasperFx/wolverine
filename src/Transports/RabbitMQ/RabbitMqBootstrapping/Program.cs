

#region sample_configuring_connection_to_rabbit_mq

using JasperFx;
using Wolverine;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    // Using the Rabbit MQ URI specification: https://www.rabbitmq.com/uri-spec.html
    opts.UseRabbitMq(new Uri(builder.Configuration["rabbitmq"]));

    // Or connect locally as you might for development purposes
    opts.UseRabbitMq();

    // Or do it more programmatically:
    opts.UseRabbitMq(rabbit =>
    {
        rabbit.HostName = builder.Configuration["rabbitmq_host"];
        rabbit.VirtualHost = builder.Configuration["rabbitmq_virtual_host"];
        rabbit.UserName = builder.Configuration["rabbitmq_username"];

        // and you get the point, you get full control over the Rabbit MQ
        // connection here for the times you need that
    });
});

#endregion

var app = builder.Build();

// Some HTTP endpoints maybe?

await app.RunJasperFxCommands(args);