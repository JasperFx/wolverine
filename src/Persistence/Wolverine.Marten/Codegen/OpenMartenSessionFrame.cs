using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Marten.Codegen;

internal class OpenMartenSessionFrame : AsyncFrame
{
    private Variable? _context;
    private Variable? _factory;
    private Variable? _martenFactory;
    private Variable _tenantId;

    public OpenMartenSessionFrame(Type sessionType)
    {
        ReturnVariable = new Variable(sessionType, this);
    }

    public Variable ReturnVariable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var methodName = ReturnVariable.VariableType == typeof(IQuerySession)
            ? nameof(OutboxedSessionFactory.QuerySession)
            : nameof(OutboxedSessionFactory.OpenSession);

        if (_context == null)
        {
            // Just use native Marten here.
            writer.Write($"await using var {ReturnVariable.Usage} = {_martenFactory!.Usage}.{methodName}();");
        }
        else if (_tenantId == null)
        {
            writer.WriteComment("Building the Marten session");
            writer.Write($"await using var {ReturnVariable.Usage} = {_factory!.Usage}.{methodName}({_context!.Usage});");
        }
        else
        {
            writer.WriteComment("Building the Marten session using the detected tenant id");
            writer.Write($"await using var {ReturnVariable.Usage} = {_factory!.Usage}.{methodName}({_context!.Usage}, {_tenantId.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Honestly, this is mostly to get the ordering correct
        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out var tenant))
        {
            _tenantId = tenant;
            yield return _tenantId;

            // Mandatory in this case
            _context = chain.FindVariable(typeof(MessageContext));
        }
        else
        {
            // Do a Try/Find here
            _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices)
                       ?? chain.TryFindVariable(typeof(IMessageBus), VariableSource.NotServices);
        }

        if (_context != null)
        {
            yield return _context;
            _factory = chain.FindVariable(typeof(OutboxedSessionFactory));
            yield return _factory;
        }
        else
        {
            _martenFactory = chain.FindVariable(typeof(ISessionFactory));
            yield return _martenFactory;
        }
    }
}