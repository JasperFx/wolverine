using Xunit;

namespace PersistenceTests.SqlServer;

public class SqlServerEnvelopeStorageAdminTests : SqlServerContext
{
    [Fact]
    public async Task smoke_test_clear_all()
    {
        await thePersistence.ClearAllAsync();
    }
}