using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Extension methods to register NServiceBus shim interfaces with Wolverine.
/// </summary>
public static class NServiceBusShimExtensions
{
    /// <summary>
    /// Registers NServiceBus shim DI services for constructor injection.
    /// <see cref="IMessageHandlerContext"/> is automatically resolved in handler methods
    /// via code generation and does not require this call.
    /// This registers <see cref="IMessageSession"/>, <see cref="IEndpointInstance"/>,
    /// <see cref="IUniformSession"/>, and <see cref="ITransactionalSession"/> for
    /// constructor injection in services outside of message handlers.
    /// </summary>
    public static WolverineOptions UseNServiceBusShims(this WolverineOptions options)
    {
        options.Services.AddScoped<IMessageSession>(sp =>
            new WolverineMessageSession(sp.GetRequiredService<IMessageBus>()));

        options.Services.AddScoped<IEndpointInstance>(sp =>
            new WolverineEndpointInstance(
                sp.GetRequiredService<IMessageBus>(),
                sp.GetRequiredService<Microsoft.Extensions.Hosting.IHost>()));

        options.Services.AddScoped<IUniformSession>(sp =>
            new WolverineUniformSession(sp.GetRequiredService<IMessageBus>()));

        options.Services.AddScoped<ITransactionalSession>(sp =>
            new WolverineTransactionalSession(sp.GetRequiredService<IMessageBus>()));

        return options;
    }
}
