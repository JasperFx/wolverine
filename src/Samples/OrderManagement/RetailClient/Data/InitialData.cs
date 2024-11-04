using Marten;
using Marten.Schema;

namespace RetailClient.Data;

public class InitialData : IInitialData
{
    private readonly object[] _initialData;

    public InitialData(
        params object[] initialData
    )
    {
        _initialData = initialData;
    }

    public async Task Populate(
        IDocumentStore store,
        CancellationToken cancellation
    )
    {
        await using var session = store.LightweightSession();
        // Marten UPSERT will cater for existing records
        session.Store(_initialData);
        await session.SaveChangesAsync();
    }
}

public static class InitialDatasets
{
    public static readonly Customer[] Customers =
    {
        new()
        {
            Id = "janedoe@tempuri.org",
            Email = "janedoe@tempuri.org",
            Name = "Jane Doe"
        },
        new()
        {
            Id = "johndoe@tempuri.org",
            Email = "johndoe@tempuri.org",
            Name = "John Doe"
        },
        new()
        {
            Id = "alice@tempuri.org",
            Email = "alice@tempuri.org",
            Name = "Alice Doe"
        },
        new()
        {
            Id = "bob@tempuri.org",
            Email = "bob@tempuri.org",
            Name = "Bob Doe"
        }
    };
}