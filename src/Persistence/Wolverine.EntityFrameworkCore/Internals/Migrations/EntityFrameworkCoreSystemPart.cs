using JasperFx;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Environment;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.CommandLine;
using Weasel.Core.Migrations;
using Weasel.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Internals.Migrations;

public class EntityFrameworkCoreSystemPart : ISystemPart, IDatabaseSource
{
    private readonly IServiceContainer _container;
    private readonly JasperFxOptions _options;
    private readonly IDbContextBuilder[] _sources;

    public EntityFrameworkCoreSystemPart(IServiceContainer container, JasperFxOptions options)
    {
        _container = container;
        _options = options;

        _sources = container.Services.GetServices<IDbContextBuilder>().ToArray();

        Cardinality = _sources.Length switch
        {
            1 => _sources[0].Cardinality,
            > 1 => _sources.Any(x => x.Cardinality == DatabaseCardinality.DynamicMultiple)
                ? DatabaseCardinality.DynamicMultiple
                : DatabaseCardinality.StaticMultiple,
            _ => DatabaseCardinality.Single
        };
    }

    public string Title { get; } = "Entity Core Framework";
    public Uri SubjectUri { get; } = new Uri("efcore://");
    public async Task WriteToConsole()
    {
        var databases = await BuildDatabases();

        var description = OptionsDescription.For(this);
        description
            .AddChildSet("DbContexts")
            .Rows
            .AddRange(databases.Select(x => x.Describe()));
        
        OptionDescriptionWriter.Write(description);
    }

    public async ValueTask<IReadOnlyList<IStatefulResource>> FindResources()
    {
        // Only create DatabaseResource instances for non-tenanted DbContexts.
        // Tenanted DbContexts are managed by TenantedDbContextInitializer which handles
        // both database creation and schema migration via IResourceCreator.
        var dbContextTypes = _container
            .FindMatchingServices(type => type.CanBeCastTo<DbContext>())
            .Where(x => !x.IsKeyedService)
            .Select(x => x.ServiceType)
            .ToArray();

        var list = new List<IStatefulResource>();
        using var scope = _container.GetInstance<IServiceScopeFactory>().CreateScope();

        foreach (var dbContextType in dbContextTypes)
        {
            var matching = _sources.FirstOrDefault(x => x.DbContextType == dbContextType);
            if (matching == null)
            {
                var context = (DbContext)scope.ServiceProvider.GetRequiredService(dbContextType);
                var database = _container.Services.CreateDatabase(context, dbContextType.FullNameInCode());
                list.Add(new DatabaseResource(database, SubjectUri));
            }
        }

        return list;
    }

    public async Task AssertEnvironmentAsync(IServiceProvider services, EnvironmentCheckResults results, CancellationToken token)
    {
        var databases = await BuildDatabases();
        foreach (var database in databases)
        {
            try
            {
                await database.AssertConnectivityAsync(token);
                results.RegisterSuccess("Able to connect to " + database.Describe().DatabaseUri());
            }
            catch (Exception e)
            {
                results.RegisterFailure("Unable to connect to " + database.Describe().DatabaseUri(), e);
            }
        }
    }

    public DatabaseCardinality Cardinality { get; }
    public async ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token)
    {
        var databases = await BuildDatabases();
        return new DatabaseUsage
        {
            Cardinality = Cardinality,
            MainDatabase = databases.Count == 1 ? databases[0].Describe() : null,
            Databases = databases.Select(x => x.Describe()).ToList()
        };
    }

    public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        var dbContextTypes = _container
            .FindMatchingServices(type => type.CanBeCastTo<DbContext>()).Where(x => !x.IsKeyedService).Select(x => x.ServiceType).ToArray();
        var list = new List<IDatabaseWithTables>();

        using var scope = _container.GetInstance<IServiceScopeFactory>().CreateScope();

        foreach (var dbContextType in dbContextTypes)
        {
            var matching = _sources.FirstOrDefault(x => x.DbContextType == dbContextType);
            if (matching == null)
            {
                var context = (DbContext)scope.ServiceProvider.GetRequiredService(dbContextType);
                var database = _container.Services.CreateDatabase(context, dbContextType.FullNameInCode());
                list.Add(database);
            }
            else
            {
                var contexts = await matching.FindAllAsync();
                foreach (var dbContext in contexts)
                {
                    await scope.ServiceProvider.EnsureDatabaseExistsAsync(dbContext);
                    var database = _container.Services.CreateDatabase(dbContext, dbContextType.FullNameInCode());
                    list.Add(database);
                }
            }
        }

        return list;
    }
}