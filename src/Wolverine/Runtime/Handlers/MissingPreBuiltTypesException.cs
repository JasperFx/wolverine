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

    /// <summary>
    ///     GH-5523: closes every missing-pre-built-types message. In 7.0 a Wolverine application in the Production
    ///     profile loads pre-generated code by default, so this is now the error an upgraded application meets on
    ///     its first production deploy, and it has to name both ways out.
    /// </summary>
    internal const string HowToFix =
        "Generate the code with 'dotnet run -- codegen write' and compile its output into the application assembly. " +
        "Since Wolverine 7.0 an application in the Production profile loads pre-generated code (TypeLoadMode.Static) by default; " +
        "to compile at runtime as Wolverine 6.x did, call opts.RestoreV6Defaults() in UseWolverine() and reference WolverineFx.RuntimeCompilation, " +
        "or choose the Production profile's mode explicitly with services.CritterStackDefaults(x => x.Production.GeneratedCodeMode = TypeLoadMode.Auto).";
}
