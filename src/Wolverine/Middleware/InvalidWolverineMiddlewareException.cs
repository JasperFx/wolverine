using JasperFx.Core.Reflection;

namespace Wolverine.Middleware;

public class InvalidWolverineMiddlewareException : Exception
{
    public InvalidWolverineMiddlewareException(string? message) : base(message)
    {
    }

    public InvalidWolverineMiddlewareException(Type type) : base(
        $"Type {type.FullNameInCode()} is not valid as Wolverine middleware. Middleware classes must be public, and have any mix of Before/BeforeAsync/After/AfterAsync/Finally/FinallyAsync methods")
    {
    }
}