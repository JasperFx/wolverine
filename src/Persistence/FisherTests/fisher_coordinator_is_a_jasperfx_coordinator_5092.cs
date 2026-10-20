using Shouldly;

namespace FisherTests;

// GH-5092: Wolverine.Fisher's coordinator was its own interface, so a store-agnostic caller (CritterWatch,
// Marten-style projection diagnostics) walking for JasperFx.Events.Daemon.IProjectionCoordinator could not
// find a Fisher store's coordinator at all.
public class fisher_coordinator_is_a_jasperfx_coordinator_5092
{
    [Fact]
    public void the_fisher_coordinator_interfaces_extend_the_jasperfx_one()
    {
        typeof(JasperFx.Events.Daemon.IProjectionCoordinator)
            .IsAssignableFrom(typeof(Wolverine.Fisher.Distribution.IProjectionCoordinator))
            .ShouldBeTrue();

        typeof(JasperFx.Events.Daemon.IProjectionCoordinator)
            .IsAssignableFrom(typeof(Wolverine.Fisher.Distribution.IProjectionCoordinator<global::Fisher.IDocumentStore>))
            .ShouldBeTrue();
    }
}
