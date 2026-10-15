using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-5049. IMessageInbox.IsHandledAsync has a default implementation, so a store that does not override it still
/// compiles and answers the way the push receiver's inbox deduplication did before: any existing row counts as handled
/// </summary>
public class message_inbox_is_handled_default_5049
{
    // Re-implements IMessageInbox so ExistsAsync answers true, and leaves IsHandledAsync to the default
    private class ExistsOnlyInbox : NullMessageStore, IMessageInbox
    {
        public bool Exists { get; set; }

        public new Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
            => Task.FromResult(Exists);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task the_default_answers_from_exists(bool exists)
    {
        IMessageInbox inbox = new ExistsOnlyInbox { Exists = exists };

        (await inbox.IsHandledAsync(ObjectMother.Envelope(), TestContext.Current.CancellationToken)).ShouldBe(exists);
    }
}
