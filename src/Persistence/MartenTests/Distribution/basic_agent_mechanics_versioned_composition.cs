using JasperFx.Core;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.Support;
using MartenTests.Distribution.TripDomain;
using Wolverine;
using Xunit.Abstractions;

namespace MartenTests.Distribution;

public class basic_agent_mechanics_versioned_composition(ITestOutputHelper output) : MultiTenantContext(output)
{
    [Fact]
    public async Task start_with_multiple_databases_on_one_single_node()
    {
        await tenancy.AddDatabaseRecordAsync("tenant1", tenant1ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant2", tenant2ConnectionString);

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = theDistributor.Scheme;

            // 3 projections x 2 databases = 6 total
            w.ExpectRunningAgents(theOriginalHost, 6);
        }, 30.Seconds());
    }
    
    [Fact]
    public async Task spread_databases_out_via_host()
    {
        await tenancy.AddDatabaseRecordAsync("tenant1", tenant1ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant2", tenant2ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant3", tenant3ConnectionString);
        await tenancy.AddDatabaseRecordAsync("tenant4", tenant4ConnectionString);
        
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = theDistributor.Scheme;
        
            // 3 projections x 2 databases = 6 total
            w.ExpectRunningAgents(theOriginalHost, 12);
        }, 30.Seconds());

        var host2 = await startHostAsync();
        

        var host3 = await startHostAsync();
        var host4 = await startHostAsync();
        
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = theDistributor.Scheme;

            // 3 projections x 2 databases = 6 total
            w.ExpectRunningAgents(theOriginalHost, 3);
            w.ExpectRunningAgents(host2, 3);
            w.ExpectRunningAgents(host3, 3);
            w.ExpectRunningAgents(host4, 3);
        }, 30.Seconds());
    }


    protected override void SetupProjections(StoreOptions storeOptions)
    {
        storeOptions.Projections.CompositeProjectionFor("Trips", x =>
        {
            x.Version = 2;
            x.Add<TripProjection>();
            x.Add<DayProjection>();
            x.Add<DistanceProjection>();
        });
    }
}