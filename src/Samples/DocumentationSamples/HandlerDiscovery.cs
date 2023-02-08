using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using TestingSupport.Compliance;
using Wolverine;

namespace DocumentationSamples;

#region sample_SimpleHandler

public class SimpleHandler
{
    public void Handle(PingMessage message)
    {
        Console.WriteLine("I got a ping!");
    }
}

#endregion

public class PongWriter : IPongWriter
{
    public Task WritePong(PongMessage message)
    {
        return Task.CompletedTask;
    }
}

#region sample_AsyncHandler

public interface IPongWriter
{
    Task WritePong(PongMessage message);
}

public class AsyncHandler
{
    private readonly IPongWriter _writer;

    public AsyncHandler(IPongWriter writer)
    {
        _writer = writer;
    }

    public Task Handle(PongMessage message)
    {
        return _writer.WritePong(message);
    }
}

#endregion

#region sample_Handlers_IMessage

public interface IMessage
{
}

public class MessageOne : IMessage
{
}

#endregion

#region sample_Handlers_GenericMessageHandler

public class GenericMessageHandler
{
    public void Consume(IMessage messagem, Envelope envelope)
    {
        Console.WriteLine($"Got a message from {envelope.Source}");
    }
}

#endregion

#region sample_Handlers_SpecificMessageHandler

public class SpecificMessageHandler
{
    public void Consume(MessageOne message)
    {
    }
}

#endregion

public class MyService : IMyService
{
}

#region sample_injecting_services_into_handlers

public interface IMyService
{
}

public class ServiceUsingHandler
{
    private readonly IMyService _service;

    // Using constructor injection to get dependencies
    public ServiceUsingHandler(IMyService service)
    {
        _service = service;
    }

    public void Consume(PingMessage message)
    {
        // do stuff using IMyService with the PingMessage
        // input
    }
}

#endregion

#region sample_IHandler_of_T

public interface IHandler<T>
{
    void Handle(T message);
}

#endregion

internal static class HandlerSamples
{
    public static async Task custom_handler_config()
    {
        #region sample_CustomHandlerApp

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.Discovery(x =>
                {
                    // Turn off the default handler conventions
                    // altogether
                    x.DisableConventionalDiscovery();

                    // Include candidate actions by a user supplied
                    // type filter
                    x.IncludeTypes(t => t.IsInNamespace("MyApp.Handlers"));

                    // Include candidate classes by suffix
                    x.IncludeClassesSuffixedWith("Listener");

                    // Include a specific handler class with a generic argument
                    x.IncludeType<SimpleHandler>();
                });
            }).StartAsync();

        #endregion
    }
}