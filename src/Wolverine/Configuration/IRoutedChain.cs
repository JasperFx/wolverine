namespace Wolverine.Configuration;

/// <summary>
///     Implemented by chains that are matched from a URL route template — today only Wolverine.Http's
///     <c>HttpChain</c>. Middleware that resolves a route value through its own frames instead of through
///     the endpoint method signature uses this to tell the chain the real CLR type behind a route
///     parameter, so that the generated OpenAPI metadata for that parameter isn't degraded to
///     <c>string</c>.
/// </summary>
/// <remarks>
///     The motivating case is the Marten/Polecat aggregate handler workflow (see GH-3420): given
///     <c>[AggregateHandler, WolverinePost("/orders/{id}/confirm")] Handle(ConfirmOrder command, Order order)</c>,
///     nothing in the endpoint signature binds <c>{id}</c> — the aggregate id is resolved from the command
///     during code generation — and the aggregate's identity type is domain knowledge that Wolverine.Http
///     cannot infer on its own.
/// </remarks>
public interface IRoutedChain
{
    /// <summary>
    ///     The names of the parameters declared in this chain's route template. "/orders/{id}/confirm"
    ///     yields "id".
    /// </summary>
    IReadOnlyList<string> RouteParameterNames { get; }

    /// <summary>
    ///     Tell this chain the CLR type behind a named route parameter. Purely descriptive: this creates no
    ///     binding and does not affect the generated code. Ignored when the route template has no parameter
    ///     by that name.
    /// </summary>
    void DeclareRouteParameterType(string routeParameterName, Type parameterType);
}
