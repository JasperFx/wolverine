namespace Wolverine.Runtime.Handlers;

/// <summary>
///     Thrown at startup when Wolverine is running in <see cref="JasperFx.CodeGeneration.TypeLoadMode.Static" />
///     but one or more of the expected pre-generated handler types cannot be loaded out of
///     <see cref="WolverineOptions.ApplicationAssembly" />. Before GH-4151 this state let the host start up
///     healthy and then threw on the first message of each affected type, from a place in the pipeline where
///     no failure policy could apply.
/// </summary>
public class MissingPreBuiltTypesException : Exception
{
    public MissingPreBuiltTypesException(string message) : base(message)
    {
    }
}
