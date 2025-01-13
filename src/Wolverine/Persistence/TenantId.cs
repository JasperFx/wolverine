using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

#region sample_TenantId

/// <summary>
/// Strong typed identifier for the tenant id within a Wolverine message handler
/// or HTTP endpoint that is using multi-tenancy
/// </summary>
/// <param name="Value">The active tenant id. Note that this can be null</param>
public record TenantId(string Value)
{
    public const string DefaultTenantId = "*DEFAULT*";

    /// <summary>
    /// Is there a non-default tenant id?
    /// </summary>
    /// <returns></returns>
    public bool IsEmpty() => Value.IsEmpty() || Value == DefaultTenantId;
}

#endregion

internal class TenantIdSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(TenantId);
    }

    public Variable Create(Type type)
    {
        return new TenantIdResolutionFrame().TenantId;
    }
}

internal class TenantIdResolutionFrame : SyncFrame
{
    private bool _useRawTenantId = false;
    private Variable _context;

    public TenantIdResolutionFrame()
    {
        TenantId = new Variable(typeof(TenantId), "tenantIdentifier", this);
    }

    public Variable TenantId { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName,
                out var rawId))
        {
            yield return rawId;
            _useRawTenantId = true;
        }
        else
        {
            _context = chain.FindVariable(typeof(IMessageContext));
            yield return _context;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_useRawTenantId)
        {
            writer.WriteLine($"var {TenantId.Usage} = new {typeof(TenantId).FullNameInCode()}({PersistenceConstants.TenantIdVariableName});");
        }
        else
        {
            writer.WriteLine($"var {TenantId.Usage} = new {typeof(TenantId).FullNameInCode()}({_context.Usage}.{nameof(IMessageContext.TenantId)});");
        }
        
        Next?.GenerateCode(method, writer);
    }
}