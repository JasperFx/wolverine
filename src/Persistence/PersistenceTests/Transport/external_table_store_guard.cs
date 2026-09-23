using NSubstitute;
using Xunit;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;

namespace PersistenceTests.Transport;

public class external_table_store_guard
{
    /// <summary>
    /// The guard in SendMessageThroughExternalTable used to null check the result of
    /// JasperFx's As&lt;T&gt;(), which is a hard cast -- so the branch was unreachable and a
    /// non-relational message store got a bare InvalidCastException instead of an explanation.
    /// </summary>
    [Fact]
    public async Task a_message_store_that_cannot_do_external_tables_gets_an_explanation()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());

        // Deliberately NOT an IExternalDbTransportStore
        var storage = Substitute.For<IMessageStore>();
        runtime.Storage.Returns(storage);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runtime.SendMessageThroughExternalTable("some.table", new SomeExternalMessage()));

        ex.Message.ShouldContain("relational database message storage");
    }
}

public record SomeExternalMessage;
