using Marten;

namespace Wolverine.Marten;

public interface IMartenAction : ISideEffect
{
    void Execute(IDocumentSession session);
}

public static class MartenOps
{
    public static StoreDocument Store<T>(T document) where T : notnull
    {
        return new StoreDocument<T>(document);
    }
}

public class StoreDocument<T> : StoreDocument where T : notnull
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