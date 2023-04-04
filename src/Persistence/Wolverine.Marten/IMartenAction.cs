using Marten;

namespace Wolverine.Marten;

public interface IMartenAction : ISideEffect
{
    void Execute(IDocumentSession session);
}

public static class MartenOperations
{
    public static StoreDocument Store<T>(T document)
    {
        return new StoreDocument<T>(document);
    }
}

public class StoreDocument<T> : StoreDocument
{
    public StoreDocument(T document)
    {
        Document = document;
    }

    public T Document { get; }

    public void Execute(IDocumentSession session)
    {
        session.Store(Document);
    }

    object StoreDocument.Document => Document;
}

// ReSharper disable once InconsistentNaming
public interface StoreDocument : IMartenAction
{
    object Document { get; }
}