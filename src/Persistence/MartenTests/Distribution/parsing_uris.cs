using Shouldly;
using Wolverine.Marten.Distribution;

namespace MartenTests.Distribution;

public class parsing_uris
{
    [Fact]
    public void round_trip_uri()
    {
        var uri = ProjectionAgents.UriFor("db1", "Proj1:V2:All");
        var (databaseName, identity) = ProjectionAgents.Parse(uri);
        
        databaseName.ShouldBe("db1");
        identity.ShouldBe("Proj1:V2:All");
    }
}