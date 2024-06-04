using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Marten;
using Marten.Storage.Metadata;

namespace Wolverine.Http.Marten;

internal class SetVariableToNullIfSoftDeletedFrame : SyncFrame
{
    private readonly Type _entityType;
    private Variable _entity;
    private Variable _documentSession;
    private Variable _entityMetadata;

    public SetVariableToNullIfSoftDeletedFrame(Type entityType)
    {
        _entityType = entityType;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("If the document is soft deleted, set the variable to null");

        if (this.IsAsync)
        {
            writer.Write($"var {_entityMetadata.Usage} = {_entity.Usage} != null");
            writer.Write($"    ? await {_documentSession.Usage}.{nameof(IDocumentSession.MetadataForAsync)}({_entity.Usage}).ConfigureAwait(false)");
            writer.Write($"    : null;");
        }
        else
        {
            writer.Write($"var {_entityMetadata.Usage} = {_entity.Usage} != null");
            writer.Write($"    ? {_documentSession.Usage}.{nameof(IDocumentSession.MetadataFor)}({_entity.Usage})");
            writer.Write($"    : null;");
        }
            
        writer.Write($"BLOCK:if ({_entityMetadata.Usage}?.{nameof(DocumentMetadata.Deleted)} == true)");
        writer.Write($"{_entity.Usage} = null;");
        writer.FinishBlock();
            
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _entity = chain.FindVariable(_entityType);
        yield return _entity;

        _documentSession = chain.FindVariable(typeof(IDocumentSession));
        yield return _documentSession;

        _entityMetadata = new Variable(typeof(DocumentMetadata), _entity.Usage + "Metadata", this);
        yield return _entityMetadata;
    }
}