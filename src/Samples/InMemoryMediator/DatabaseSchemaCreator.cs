
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;

namespace InMemoryMediator;

public class DatabaseSchemaCreator : IHostedService
{
    private readonly IConfiguration _configuration;

    public DatabaseSchemaCreator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var items = new Table("items");
        items.AddColumn<Guid>("id").AsPrimaryKey();
        items.AddColumn<string>("name");

        var connectionString = _configuration.GetConnectionString("SqlServer");
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        
        await items.ApplyChangesAsync(conn, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}