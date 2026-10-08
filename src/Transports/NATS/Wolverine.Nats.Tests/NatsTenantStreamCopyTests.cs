using Shouldly;
using NATS.Client.JetStream.Models;
using Wolverine.Nats.Configuration;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A tenant with its own connection provisions the transport's declared streams from a copy of them. The copy has
/// to carry every setting of a declaration -- a mirror, its sources, its placement included -- and be a copy, so a
/// tenant action changing it does not reach the transport or another tenant.
/// </summary>
public class NatsTenantStreamCopyTests
{
    [Fact]
    public void a_tenant_copy_keeps_mirror_sources_and_placement()
    {
        var transport = new NatsTransportConfiguration();
        transport.Streams["AUDIT"] = new StreamConfiguration { Name = "AUDIT" }
            .MirrorOf("ORDERS", m =>
            {
                m.FilterSubject = "orders.>";
                m.External = new ExternalStreamSource { Api = "$JS.hub.API" };
                m.SubjectTransforms = [new SubjectTransform { Src = "orders.>", Dest = "audit.>" }];
            });
        transport.Streams["ALL"] = new StreamConfiguration { Name = "ALL", Subjects = ["all.>"] }
            .SourcedFrom("ORDERS")
            .SourcedFrom("INVOICES")
            .PlacedOn("east", "ssd");

        var tenant = transport.CopyForTenant();

        var audit = tenant.Streams["AUDIT"];
        audit.Mirror.ShouldNotBeNull();
        audit.Mirror.Name.ShouldBe("ORDERS");
        audit.Mirror.FilterSubject.ShouldBe("orders.>");

        var all = tenant.Streams["ALL"];
        all.Sources.Select(x => x.Name).ShouldBe(["ORDERS", "INVOICES"]);
        all.Placement.ShouldNotBeNull();
        all.Placement.Cluster.ShouldBe("east");
        all.Placement.Tags.ShouldBe(["ssd"]);

        // A tenant action changing its copy leaves the transport's declaration alone
        audit.Mirror.Name = "OTHER";
        audit.Mirror.External!.Api = "$JS.tenant.API";
        audit.Mirror.SubjectTransforms!.First().Dest = "other.>";
        all.Sources.Clear();
        all.Placement.Tags!.Add("hdd");

        transport.Streams["AUDIT"].Mirror!.Name.ShouldBe("ORDERS");
        transport.Streams["AUDIT"].Mirror!.External!.Api.ShouldBe("$JS.hub.API");
        transport.Streams["AUDIT"].Mirror!.SubjectTransforms!.Single().Dest.ShouldBe("audit.>");
        transport.Streams["ALL"].Sources.Count.ShouldBe(2);
        transport.Streams["ALL"].Placement!.Tags.ShouldBe(["ssd"]);
    }
}
