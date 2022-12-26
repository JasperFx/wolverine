using JasperFx.CodeGeneration;

namespace Wolverine.Middleware;

public class InvalidWolverineMiddlewareException : Exception
{
    public InvalidWolverineMiddlewareException(Type type) : base(
        $"Type {type.FullNameInCode()} is not valid as Wolverine middleware. Middleware classes must be public, and have any mix of Before/BeforeAsync/After/AfterAsync methods")
    {
    }
}