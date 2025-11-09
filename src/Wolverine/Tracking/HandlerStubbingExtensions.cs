using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Stubs;

namespace Wolverine.Tracking;

public static class HandlerStubbingExtensions
{
    /// <summary>
    /// Configure stubbed message handlers within an application using Wolverine. This is mostly meant
    /// to allow you to stub out request/reply calls to external systems, but can also be used to fake
    /// internal message handling when publishing messages -- but will not work for IMessageBus.InvokeAsync()!
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public static void WolverineStubs(this IServiceProvider services, Action<IStubHandlers> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure(services.GetRequiredService<IWolverineRuntime>().Stubs);
    }
    
    /// <summary>
    /// Configure stubbed message handlers within an application using Wolverine. This is mostly meant
    /// to allow you to stub out request/reply calls to external systems, but can also be used to fake
    /// internal message handling when publishing messages -- but will not work for IMessageBus.InvokeAsync()!
    /// </summary>
    /// <param name="host"></param>
    /// <param name="configure"></param>
    public static void WolverineStubs(this IHost host, Action<IStubHandlers> configure)
    {
        host.Services.WolverineStubs(configure);
    }

    /// <summary>
    /// Clears out any registered message handler stubs in this Wolverine application and restores
    /// the system to its original configuration
    /// </summary>
    /// <param name="services"></param>
    public static void ClearAllWolverineStubs(this IServiceProvider services)
    {
        services.WolverineStubs(x => x.ClearAll());
    }
    
    /// <summary>
    /// Clears out any registered message handler stubs in this Wolverine application and restores
    /// the system to its original configuration
    /// </summary>
    /// <param name="host"></param>
    public static void ClearAllWolverineStubs(this IHost host)
    {
        host.Services.ClearAllWolverineStubs();
    }

    /// <summary>
    /// Register stubbed behavior within the Wolverine application for any messages of the TRequest type
    /// Use this to efficiently replace InvokeAsync<TResponse>(TRequest) calls to external services inside
    /// of automated tests
    /// </summary>
    /// <param name="services"></param>
    /// <param name="func"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    public static void StubWolverineMessageHandling<TRequest, TResponse>(this IServiceProvider services,
        Func<TRequest, TResponse> func)
    {
        services.WolverineStubs(stubs =>
        {
            stubs.Stub(func);
        });
    }
    
    /// <summary>
    /// Register stubbed behavior within the Wolverine application for any messages of the TRequest type
    /// Use this to efficiently replace InvokeAsync<TResponse>(TRequest) calls to external services inside
    /// of automated tests
    /// </summary>
    /// <param name="host"></param>
    /// <param name="func"></param>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    public static void StubWolverineMessageHandling<TRequest, TResponse>(this IHost host,
        Func<TRequest, TResponse> func)
    {
        host.Services.WolverineStubs(stubs =>
        {
            stubs.Stub(func);
        });
    }

}