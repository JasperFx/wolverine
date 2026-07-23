using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Oracle;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace OracleTests;

// GH-3589: the durability agent family (MessageStoreCollection) registers under the
// "wolverinedb" scheme, and a message store's Uri IS its agent Uri. If OracleMessageStore
// hands out a "wolverine://messages/main" or "messagedb://..." Uri, the NodeAgentController
// cannot resolve a family and throws "Unrecognized agent scheme", so the Oracle durability
// agent never starts. These tests pin the Uri to the registered agent scheme for every role.
public class message_store_uri_scheme
{
    private static IWolverineRuntime buildRuntime()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Services.Returns(new ServiceCollection().BuildServiceProvider());

        return runtime;
    }

    private static IMessageStore buildStore(MessageStoreRole role)
    {
        var options = new WolverineOptions();
        var persistence = (OracleBackedPersistence)options.PersistMessagesWithOracle(
            Servers.OracleConnectionString, "wolverine", role);

        return persistence.BuildMessageStore(buildRuntime());
    }

    [Fact]
    public void main_store_uses_the_registered_agent_scheme()
    {
        var store = buildStore(MessageStoreRole.Main);

        store.Uri.Scheme.ShouldBe(PersistenceConstants.AgentScheme);
    }

    [Fact]
    public void ancillary_store_uses_the_registered_agent_scheme()
    {
        var store = buildStore(MessageStoreRole.Ancillary);

        store.Uri.Scheme.ShouldBe(PersistenceConstants.AgentScheme);
    }

    [Fact]
    public void main_store_keeps_its_diagnostic_subject_uri()
    {
        var store = buildStore(MessageStoreRole.Main);

        // The old "wolverine://messages/main" value lives on as the diagnostic identity only,
        // never as the agent Uri.
        store.Describe().SubjectUri.ShouldBe(new Uri("wolverine://messages/main"));
    }
}
