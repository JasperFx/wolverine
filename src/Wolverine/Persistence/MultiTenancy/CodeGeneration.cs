using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;

namespace Wolverine.Persistence.MultiTenancy;

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