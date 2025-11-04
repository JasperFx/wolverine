using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Stubs;

namespace Wolverine.Tracking;

public static class WolverineHostMessageTrackingExtensions
{
    /// <summary>
    /// Fetch the WolverineRuntime for a given IHost. Useful for testing
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    public static WolverineRuntime GetRuntime(this IHost host)
    {
        return (WolverineRuntime)host.Get<IWolverineRuntime>();
    }

    /// <summary>
    /// Retrieves the assigned node number for this host. Maybe not be
    /// useful outside of tests for Wolverine itself:)
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    public static int NodeNumber(this IHost host)
    {
        return host.GetRuntime().Options.Durability.AssignedNodeNumber;
    }

    /// <summary>
    ///     Advanced usage of the 'ExecuteAndWait()' message tracking and coordination for automated testing.
    ///     Use this configuration if you want to coordinate message tracking across multiple Wolverine
    ///     applications running in the same process
    /// </summary>
    /// <param name="host"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration TrackActivity(this IHost host)
    {
        var session = new TrackedSession(host);
        return new TrackedSessionConfiguration(session);
    }

    /// <summary>
    ///     Start a new tracked session with an explicit timeout
    /// </summary>
    /// <param name="host"></param>
    /// <param name="trackingTimeout"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration TrackActivity(this IHost host, TimeSpan trackingTimeout)
    {
        var session = new TrackedSession(host);
        session.Timeout = trackingTimeout;
        return new TrackedSessionConfiguration(session);
    }

    /// <summary>
    ///     Send a message through the service bus and wait until that message
    ///     and all cascading messages have been successfully processed
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Task<ITrackedSession> SendMessageAndWaitAsync<T>(this IHost host, T? message,
        DeliveryOptions? options = null,
        int timeoutInMilliseconds = 5000)
    {
        return host.ExecuteAndWaitValueTaskAsync(c => c.SendAsync(message, options), timeoutInMilliseconds);
    }

    /// <summary>
    ///     Invoke the given message and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static Task<ITrackedSession> InvokeMessageAndWaitAsync(this IHost host, object message,
        int timeoutInMilliseconds = 5000)
    {
        return host.ExecuteAndWaitAsync(c => c.InvokeAsync(message), timeoutInMilliseconds);
    }

    /// <summary>
    ///     Invoke the given message and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <param name="tenantId"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static Task<ITrackedSession> InvokeMessageAndWaitAsync(this IHost host, object message,
        string tenantId,
        int timeoutInMilliseconds = 5000)
    {
        return host.ExecuteAndWaitAsync(c =>
        {
            c.TenantId = tenantId;
            return c.InvokeAsync(message);
        }, timeoutInMilliseconds);
    }

    /// <summary>
    ///     Invoke the given message with the expectation of a result T and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static async Task<(ITrackedSession, T?)> InvokeMessageAndWaitAsync<T>(this IHost host, object message,
        int timeoutInMilliseconds = 5000)
    {
        T? returnValue = default;
        var tracked = await host.ExecuteAndWaitAsync(async c => returnValue = await c.InvokeAsync<T>(message),
            timeoutInMilliseconds);

        return (tracked, returnValue);
    }

    /// <summary>
    ///     Invoke the given message with the expectation of a result T and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="host"></param>
    /// <param name="message"></param>
    /// <param name="tenantId"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static async Task<(ITrackedSession, T?)> InvokeMessageAndWaitAsync<T>(this IHost host, object message,
        string tenantId,
        int timeoutInMilliseconds = 5000)
    {
        T? returnValue = default;
        var tracked = await host.ExecuteAndWaitAsync(async c =>
        {
            c.TenantId = tenantId;
            returnValue = await c.InvokeAsync<T>(message);
        }, timeoutInMilliseconds);

        return (tracked, returnValue);
    }

    /// <summary>
    ///     Executes an action and waits until the execution of all messages and all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="action"></param>
    /// <returns></returns>
    public static Task<ITrackedSession> ExecuteAndWaitAsync(this IHost host, Func<Task> action,
        int timeoutInMilliseconds = 5000)
    {
        return host.ExecuteAndWaitAsync(_ => action(), timeoutInMilliseconds);
    }

    /// <summary>
    ///     Executes an action and waits until the execution of all messages and all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="action"></param>
    /// <returns></returns>
    public static async Task<ITrackedSession> ExecuteAndWaitAsync(this IHost host,
        Func<IMessageContext, Task> action,
        int timeoutInMilliseconds = 5000)
    {
        var session = new TrackedSession(host)
        {
            Timeout = timeoutInMilliseconds.Milliseconds(),
            Execution = action
        };

        await session.ExecuteAndTrackAsync();

        return session;
    }

    /// <summary>
    ///     Executes an action and waits until the execution of all messages and all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="action"></param>
    /// <returns></returns>
    internal static async Task<ITrackedSession> ExecuteAndWaitValueTaskAsync(this IHost host,
        Func<IMessageContext, ValueTask> action,
        int timeoutInMilliseconds = 5000)
    {
        var session = new TrackedSession(host)
        {
            Timeout = timeoutInMilliseconds.Milliseconds(),
            Execution = c => action(c).AsTask()
        };

        await session.ExecuteAndTrackAsync();

        return session;
    }
}

public static class WolverineHostTrackingByServiceProviderExtensions
{
        /// <summary>
    ///     Advanced usage of the 'ExecuteAndWait()' message tracking and coordination for automated testing.
    ///     Use this configuration if you want to coordinate message tracking across multiple Wolverine
    ///     applications running in the same process
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration TrackActivity(this IServiceProvider services)
    {
        var session = new TrackedSession(services);
        return new TrackedSessionConfiguration(session);
    }

    /// <summary>
    ///     Start a new tracked session with an explicit timeout
    /// </summary>
    /// <param name="services"></param>
    /// <param name="trackingTimeout"></param>
    /// <returns></returns>
    public static TrackedSessionConfiguration TrackActivity(this IServiceProvider services, TimeSpan trackingTimeout)
    {
        var session = new TrackedSession(services);
        session.Timeout = trackingTimeout;
        return new TrackedSessionConfiguration(session);
    }

    /// <summary>
    ///     Send a message through the service bus and wait until that message
    ///     and all cascading messages have been successfully processed
    /// </summary>
    /// <param name="services"></param>
    /// <param name="message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static Task<ITrackedSession> SendMessageAndWaitAsync<T>(this IServiceProvider services, T? message,
        DeliveryOptions? options = null,
        int timeoutInMilliseconds = 5000)
    {
        return services.ExecuteAndWaitValueTaskAsync(c => c.SendAsync(message, options), timeoutInMilliseconds);
    }

    /// <summary>
    ///     Invoke the given message and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="services"></param>
    /// <param name="message"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static Task<ITrackedSession> InvokeMessageAndWaitAsync(this IServiceProvider services, object message,
        int timeoutInMilliseconds = 5000)
    {
        return services.ExecuteAndWaitAsync(c => c.InvokeAsync(message), timeoutInMilliseconds);
    }

    /// <summary>
    ///     Invoke the given message and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="services"></param>
    /// <param name="message"></param>
    /// <param name="tenantId"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static Task<ITrackedSession> InvokeMessageAndWaitAsync(this IServiceProvider services, object message,
        string tenantId,
        int timeoutInMilliseconds = 5000)
    {
        return services.ExecuteAndWaitAsync(c =>
        {
            c.TenantId = tenantId;
            return c.InvokeAsync(message);
        }, timeoutInMilliseconds);
    }

    /// <summary>
    ///     Invoke the given message with the expectation of a result T and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="services"></param>
    /// <param name="message"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static async Task<(ITrackedSession, T?)> InvokeMessageAndWaitAsync<T>(this IServiceProvider services, object message,
        int timeoutInMilliseconds = 5000)
    {
        T? returnValue = default;
        var tracked = await services.ExecuteAndWaitAsync(async c => returnValue = await c.InvokeAsync<T>(message),
            timeoutInMilliseconds);

        return (tracked, returnValue);
    }

    /// <summary>
    ///     Invoke the given message with the expectation of a result T and wait until all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="services"></param>
    /// <param name="message"></param>
    /// <param name="tenantId"></param>
    /// <param name="timeoutInMilliseconds"></param>
    /// <returns></returns>
    public static async Task<(ITrackedSession, T?)> InvokeMessageAndWaitAsync<T>(this IServiceProvider services, object message,
        string tenantId,
        int timeoutInMilliseconds = 5000)
    {
        T? returnValue = default;
        var tracked = await services.ExecuteAndWaitAsync(async c =>
        {
            c.TenantId = tenantId;
            returnValue = await c.InvokeAsync<T>(message);
        }, timeoutInMilliseconds);

        return (tracked, returnValue);
    }

    /// <summary>
    ///     Executes an action and waits until the execution of all messages and all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="action"></param>
    /// <returns></returns>
    public static Task<ITrackedSession> ExecuteAndWaitAsync(this IServiceProvider services, Func<Task> action,
        int timeoutInMilliseconds = 5000)
    {
        return services.ExecuteAndWaitAsync(_ => action(), timeoutInMilliseconds);
    }

    /// <summary>
    ///     Executes an action and waits until the execution of all messages and all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="action"></param>
    /// <returns></returns>
    public static async Task<ITrackedSession> ExecuteAndWaitAsync(this IServiceProvider services,
        Func<IMessageContext, Task> action,
        int timeoutInMilliseconds = 5000)
    {
        var session = new TrackedSession(services)
        {
            Timeout = timeoutInMilliseconds.Milliseconds(),
            Execution = action
        };

        await session.ExecuteAndTrackAsync();

        return session;
    }

    /// <summary>
    ///     Executes an action and waits until the execution of all messages and all cascading messages
    ///     have completed
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="action"></param>
    /// <returns></returns>
    internal static async Task<ITrackedSession> ExecuteAndWaitValueTaskAsync(this IServiceProvider services,
        Func<IMessageContext, ValueTask> action,
        int timeoutInMilliseconds = 5000)
    {
        var session = new TrackedSession(services)
        {
            Timeout = timeoutInMilliseconds.Milliseconds(),
            Execution = c => action(c).AsTask()
        };

        await session.ExecuteAndTrackAsync();

        return session;
    }

    public static void StubHandlers(this IServiceProvider services, Action<IStubHandlers> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure(services.GetRequiredService<IWolverineRuntime>().Stubs);
    }
    
    public static void StubHandlers(this IHost host, Action<IStubHandlers> configure)
    {
        host.Services.StubHandlers(configure);
    }

    public static void ClearAllStubHandlers(this IServiceProvider services)
    {
        services.StubHandlers(x => x.ClearAll());
    }
    
    public static void ClearAllStubHandlers(this IHost host)
    {
        host.Services.ClearAllStubHandlers();
    }

    public static void StubMessageHandler<TRequest, TResponse>(this IServiceProvider services,
        Func<TRequest, TResponse> func)
    {
        services.StubHandlers(stubs =>
        {
            stubs.Stub(func);
        });
    }
    
    public static void StubMessageHandler<TRequest, TResponse>(this IHost host,
        Func<TRequest, TResponse> func)
    {
        host.Services.StubHandlers(stubs =>
        {
            stubs.Stub(func);
        });
    }
    
    
}