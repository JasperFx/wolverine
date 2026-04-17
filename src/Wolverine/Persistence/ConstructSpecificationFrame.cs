using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

/// <summary>
/// Constructs a query specification (e.g. a Marten compiled query / query plan or
/// a Wolverine.EntityFrameworkCore <see cref="EntityFrameworkCore.IQueryPlan{TDbContext,TResult}"/>)
/// at codegen time. Supports both constructor injection (resolve ctor parameters
/// from other variables in the method) and property injection (resolve public
/// settable properties — the canonical pattern for Marten compiled queries).
/// <para>
/// Used by <see cref="FromQuerySpecificationAttribute"/> to build the spec
/// instance, which is then handed to an <see cref="IPersistenceFrameProvider"/>
/// for execution.
/// </para>
/// </summary>
public class ConstructSpecificationFrame : SyncFrame
{
    private readonly Variable[] _ctorArgs;
    private readonly (string PropertyName, Variable Source)[] _propertyAssignments;

    public ConstructSpecificationFrame(
        Type specificationType,
        Variable[] ctorArgs,
        (string PropertyName, Variable Source)[] propertyAssignments,
        string variableName)
    {
        if (specificationType == null) throw new ArgumentNullException(nameof(specificationType));
        _ctorArgs = ctorArgs ?? throw new ArgumentNullException(nameof(ctorArgs));
        _propertyAssignments = propertyAssignments ?? throw new ArgumentNullException(nameof(propertyAssignments));

        Spec = new Variable(specificationType, variableName, this);
    }

    /// <summary>
    /// The constructed specification variable, ready to be consumed by a
    /// provider-specific fetch frame.
    /// </summary>
    public Variable Spec { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var args = string.Join(", ", _ctorArgs.Select(a => a.Usage));
        writer.WriteLine($"var {Spec.Usage} = new {Spec.VariableType.FullNameInCode()}({args});");

        foreach (var (propertyName, source) in _propertyAssignments)
        {
            writer.WriteLine($"{Spec.Usage}.{propertyName} = {source.Usage};");
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var a in _ctorArgs) yield return a;
        foreach (var (_, source) in _propertyAssignments) yield return source;
    }
}
