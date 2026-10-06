using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.InMemory;
using Wolverine.Persistence;

namespace Wolverine.InMemory.Codegen;

/// <summary>Loads a document -- a saga, or an <c>[Entity]</c> -- by its identity.</summary>
internal class LoadDocumentFrame : AsyncFrame
{
    private readonly Variable _id;
    private Variable? _cancellation;
    private Variable? _session;

    public LoadDocumentFrame(Type documentType, Variable id)
    {
        _id = id;
        uses.Add(id);

        var usage = $"{Variable.DefaultArgName(documentType)}_{id.Usage.Split('.').Last()}";
        Document = new Variable(documentType, usage, this);
    }

    public Variable Document { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _id;

        _session = chain.FindVariable(typeof(IInMemoryDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing document from the in-memory prototyping store");

        // The store keys a document on its boxed identity, so a strong-typed id is passed as it is: the
        // object overload resolves it the same way the document's own Id member does
        var id = _id.VariableType == typeof(Guid) || _id.VariableType == typeof(string)
            ? _id.Usage
            : $"(object){_id.Usage}";

        writer.Write(
            $"var {Document.Usage} = await {_session!.Usage}.LoadAsync<{Document.VariableType.FullNameInCode()}>({id}, {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>Queues a document operation -- Store or Delete -- on the chain's session.</summary>
internal class DocumentOperationFrame : SyncFrame
{
    private readonly string _methodName;
    private readonly Variable _document;
    private Variable? _session;

    public DocumentOperationFrame(Variable document, string methodName)
    {
        _document = document;
        _methodName = methodName;
        uses.Add(document);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IInMemoryDocumentSession));
        yield return _session;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Register the document operation with the in-memory session");
        writer.Write($"{_session!.Usage}.{_methodName}({_document.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Applies an <see cref="IStorageAction{T}"/> to an in-memory session. The store-agnostic document
/// contract has no separate insert or update, so both are a Store.
/// </summary>
public static class InMemoryStorageActionApplier
{
    public static void ApplyAction<T>(IInMemoryDocumentSession session, IStorageAction<T> action) where T : notnull
    {
        if (action.Entity == null) return;

        switch (action.Action)
        {
            case StorageAction.Delete:
                session.Delete(action.Entity);
                break;
            case StorageAction.Insert:
            case StorageAction.Store:
            case StorageAction.Update:
                session.Store(action.Entity);
                break;
        }
    }
}
