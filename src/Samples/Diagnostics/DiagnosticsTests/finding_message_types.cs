using DiagnosticsModule;
using IntegrationTests;
using Shouldly;

namespace DiagnosticsTests;

public class finding_message_types : IntegrationContext
{
    private readonly IReadOnlyList<Type> theMessages;

    public finding_message_types(AppFixture fixture) : base(fixture)
    {
        theMessages = Runtime.Options.Discovery.FindAllMessages(Runtime.Handlers);
    }

    [Fact]
    public void should_find_explicit_messages()
    {
        theMessages.ShouldContain(typeof(PublishedMessage));
    }

    [Fact]
    public void should_find_all_message_types_from_handlers()
    {
        theMessages.ShouldContain(typeof(CreateInvoice));
        theMessages.ShouldContain(typeof(StartInvoiceProcessing));
    }

    [Fact]
    public void should_find_known_messages_returned_from_cascading_results()
    {
        theMessages.ShouldContain(typeof(InvoiceCreated));
    }

    [Fact]
    public void should_find_message_types_declared_from_tuple_return_values()
    {
        theMessages.ShouldContain(typeof(AssignUser));
        theMessages.ShouldContain(typeof(OrderParts));
    }

    [Fact]
    public void should_find_messages_marked_as_imessage_from_application_assembly()
    {
        theMessages.ShouldContain(typeof(InvoiceCreated));
        theMessages.ShouldContain(typeof(CreateShippingLabel));
    }

    [Fact]
    public void should_find_messages_decorated_with_wolverine_message_attribute()
    {
        theMessages.ShouldContain(typeof(AddItem));
    }

    [Fact]
    public void should_find_messages_decorated_from_other_module_assemblies()
    {
        theMessages.ShouldContain(typeof(DiagnosticsMessage1));
        theMessages.ShouldContain(typeof(DiagnosticsMessage2));
    }

    [Fact]
    public void filters_out_not_decorated_types()
    {
        theMessages.ShouldNotContain(typeof(NotDiagnosticsMessage3));
    }

    [Fact]
    public void find_messages_from_other_types()
    {
        // Because it implements IDiagnosticsMessage, which
        // is tagged as a message in the Program file
        theMessages.ShouldContain(typeof(DiagnosticsMessage4));
    }
}