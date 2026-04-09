using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

/// <summary>
///     Optional interface for handler types that want compile-time validated
///     configuration of their <see cref="HandlerChain"/>. Implementing this interface
///     ensures the <c>Configure</c> method name and signature are checked at compile time,
///     preventing silent failures from misspellings or incorrect parameter types.
/// </summary>
/// <example>
/// <code>
/// public class OrderHandler : IHandlerConfiguration
/// {
///     public void Handle(PlaceOrder command) { /* ... */ }
///
///     public static void Configure(HandlerChain chain)
///     {
///         chain.Middleware.Add(new MyMiddlewareFrame());
///         chain.SuccessLogLevel = LogLevel.Debug;
///     }
/// }
/// </code>
/// </example>
public interface IHandlerConfiguration
{
    /// <summary>
    ///     Configure the <see cref="HandlerChain"/> for this handler type. This method is
    ///     called once during Wolverine startup, before code generation.
    /// </summary>
    /// <param name="chain">The handler chain to configure.</param>
    static abstract void Configure(HandlerChain chain);
}
