using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// The specification half of Bobcat.EventModel's import tests: AggregateInferenceTests, EventModelersJsonReaderTests,
/// StreamStartIdentityTests and StrongTypedIdentityTests each asserted on the stubs AND the specifications one import
/// wrote. JasperFx/bobcat#755 kept the stub half there and the specification half moved here with the spec writer
/// (GH-5082), unchanged but for where the board comes from.
/// </summary>
public class SpecsBesideTheImportTests
{
    private static string code(GeneratedSpecs specs) => string.Join("\n", specs.Files.Select(x => x.Content));

    private static string stubs(CSharpModelWriter.Output output) => string.Join("\n", output.StubFiles.Select(x => x.Content));

    // ---------------------------------------------------------------- AggregateInferenceTests

    [Fact]
    public void a_command_drawing_on_two_streams_carries_each_ones_identity_and_the_spec_sets_both()
    {
        var board = EmlangReader.Read(Boards.Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Shelter");
        var written = stubs(CSharpModelWriter.Write(model, "Shelter", specs.Additions));
        var all = code(specs);

        written.ShouldContain("public record AcceptHomeCheckAssignment(Guid HomeCheckId, Guid VolunteerApplicationId);");
        all.ShouldContain("await GivenEvents<HomeCheck>(theHomeCheck, Specify<HomeCheckRequested>());");
        all.ShouldContain("await GivenEventsOn<VolunteerApplication>(theVolunteerApplication, Specify<VolunteerApproved>());");
        EmlangSpecWriterTests.unindented(all).ShouldContain(EmlangSpecWriterTests.unindented("""
            await WhenReceived(Specify<AcceptHomeCheckAssignment>()
                .With(x => x.HomeCheckId, theHomeCheck)
                .With(x => x.VolunteerApplicationId, theVolunteerApplication));
            """));
    }

    [Fact]
    public void a_command_drawing_on_two_streams_checks_each_stream_by_its_own_key()
    {
        // wolverine#4865: ThenEvents could only speak for one of them
        var board = EmlangReader.Read(Boards.Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var all = code(EmlangSpecWriter.Write(board, model, "Shelter"));

        all.ShouldContain("ThenEventsOn<HomeCheck>(theHomeCheck, Specify<HomeCheckAssignmentAccepted>());");
        all.ShouldContain("ThenNoEventsOn<VolunteerApplication>(theVolunteerApplication);");
        all.ShouldNotContain("ThenEvents(Specify<HomeCheckAssignmentAccepted>());");
    }

    [Fact]
    public void a_command_that_starts_a_stream_is_checked_as_one_paragraph()
    {
        // wolverine#4865: the handler assigns the stream's id, so the spec reads it back from the act
        var board = EmlangReader.Read(Boards.Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var all = code(EmlangSpecWriter.Write(board, model, "Shelter"));

        all.ShouldContain("await ThenStreamIsStartedWithEvents<VolunteerApplication>(Specify<VolunteerApplicationSubmitted>());");
    }

    [Fact]
    public void the_specs_arrange_on_the_typed_aggregate_and_address_it_by_id()
    {
        var board = EmlangReader.Read(Boards.Shelter);
        var all = code(EmlangSpecWriter.Write(board, EmlangImport.ToCurated(board, "Shelter", "Shelter").Model, "Shelter"));

        all.ShouldContain("await GivenEvents<Appointment>(theAppointment, Specify<HomeCheckAppointmentProposed>());");
        all.ShouldContain("await WhenReceived(Specify<ConfirmAppointment>().With(x => x.Id, theAppointment));");
        all.ShouldNotContain("await GivenEvents(the");
    }

    [Fact]
    public void a_type_only_an_example_names_lands_in_the_chapter_of_the_slice_whose_example_names_it()
    {
        var board = EmlangReader.Read(Boards.Shelter);
        var model = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Shelter");
        var output = CSharpModelWriter.Write(model, "Shelter", specs.Additions);

        output.Layout.FileOf("ApplicationReviewed").ShouldBe("Features/Volunteering/ApplicationReviewed.cs");
        output.Layout.NamespaceOf("ApplicationReviewed").ShouldBe("Shelter.Volunteering");
    }

    // ---------------------------------------------------------------- EventModelersJsonReaderTests

    [Fact]
    public void the_generator_writes_specs_from_a_json_export()
    {
        var board = EventModelersJsonReader.Read(Boards.Config);
        var model = EmlangImport.ToCurated(board, "Shop").Model;

        var specs = EmlangSpecWriter.Write(board, model, "Shop");

        specs.Specs.ShouldBe(2);
        code(specs).ShouldContain("ThenRefusedWith(\"Quantity over limit\");");
        code(specs).ShouldContain("await WhenReceived(Specify<AddItem>().With(x => x.CartId, theCart).With(x => x.Quantity, 2));");
    }

    // ---------------------------------------------------------------- StreamStartIdentityTests

    private static (string Stubs, string Specs) generate(EmlangBoard board, string ns = "Shelter")
    {
        var model = EmlangImport.ToCurated(board, ns, ns).Model;
        var specs = EmlangSpecWriter.Write(board, model, ns);
        return (stubs(CSharpModelWriter.Write(model, ns, specs.Additions)), code(specs));
    }

    [Fact]
    public void the_spec_reads_a_started_streams_id_from_the_act_when_the_command_cannot_carry_it()
    {
        var (written, specs) = generate(EventModelersJsonReader.Read(Boards.MintedIdentity), "Applications");
        var all = EmlangSpecWriterTests.unindented(specs);

        written.ShouldContain("public record SubmitApplication(string Areas);");

        // No value the spec declares can reach a handler that mints the id, so none is declared up front
        specs.ShouldNotContain("GivenNoEventsFor");
        specs.ShouldNotContain("Guid.CreateVersion7()");
        all.ShouldContain(EmlangSpecWriterTests.unindented(
            """
            await WhenReceived(Specify<SubmitApplication>().With(x => x.Areas, "dogs"));

            var theApplication = TheStartedStream<Application>();
            """));
        all.ShouldContain("ThenStreamIsStartedWithEvents<Application>(Specify<ApplicationSubmitted>()");
        all.ShouldContain(".With(x => x.ApplicationId, theApplication)");
    }

    [Fact]
    public void a_started_streams_id_nothing_expects_is_not_read_back()
    {
        var (_, specs) = generate(EventModelersJsonReader.Read(Boards.MintedIdentity.Replace(
            "{ \"name\": \"applicationId\", \"type\": \"UUID\", \"idAttribute\": true },", "")), "Applications");

        specs.ShouldNotContain("TheStartedStream");
        specs.ShouldContain("ThenStreamIsStartedWithEvents<Application>(");
    }

    [Fact]
    public void a_command_that_carries_the_streams_identity_still_arranges_the_empty_stream()
    {
        // The model names the identity on the command, so the spec knows the id before the act
        var (_, specs) = generate(EventModelersJsonReader.Read(Boards.MintedIdentity.Replace(
            "{ \"name\": \"areas\", \"type\": \"String\" } ] } ],\n    \"events\"",
            "{ \"name\": \"applicationId\", \"type\": \"UUID\", \"idAttribute\": true }, { \"name\": \"areas\", \"type\": \"String\" } ] } ],\n    \"events\"")),
            "Applications");

        specs.ShouldContain("await GivenNoEventsFor<Application>(theApplication);");
        specs.ShouldNotContain("TheStartedStream");
    }

    [Fact]
    public void a_string_keyed_stream_is_started_with_the_id_its_command_carries_and_the_spec_declares_it()
    {
        var (written, specs) = generate(EventModelersJsonReader.Read(Boards.StreamStartNaturalKey), "Customers");

        // A natural key cannot be minted, so the command carries it, as a string
        written.ShouldContain("public record RegisterCustomer(string Id, string Name);");
        specs.ShouldContain("await GivenNoEventsFor<Customer>(theCustomer);");
        specs.ShouldContain(".With(x => x.Id, theCustomer)");
        specs.ShouldNotContain("TheStartedStream");
    }

    // ---------------------------------------------------------------- StrongTypedIdentityTests

    private static (CSharpModelWriter.Output Stubs, GeneratedSpecs Specs) typedImport(string yaml = Boards.Ordering, bool typed = true)
    {
        var board = EmlangReader.Read(yaml);
        var imported = EmlangImport.ToCurated(board, "Shop").Model;
        var ids = typed ? StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported)) : StrongTypedIds.None;
        var specs = EmlangSpecWriter.Write(board, imported, "Shop", "Shop.Specs", null, ids);

        return (CSharpModelWriter.Write(imported, "Shop", specs.Additions, perChapter: true, ids), specs);
    }

    [Fact]
    public void a_generated_specification_declares_the_identity_as_the_typed_value()
    {
        var all = code(typedImport().Specs);

        // The half that would otherwise not compile: a Guid local handed to an OrderId member
        all.ShouldContain("var theOrder = new OrderId(Guid.CreateVersion7());");
        all.ShouldContain(".With(x => x.OrderId, theOrder)");
    }

    [Fact]
    public void without_the_flag_the_specification_declares_a_guid()
    {
        code(typedImport(typed: false).Specs).ShouldContain("var theOrder = Guid.CreateVersion7();");
    }

    [Fact]
    public void a_typed_natural_key_is_declared_as_the_identity_type()
    {
        var board = EventModelersJsonReader.Read(Boards.StrongTypedNaturalKey);
        var imported = EmlangImport.ToCurated(board, "Customers", "Customers").Model;
        var ids = StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported));
        var specs = EmlangSpecWriter.Write(board, imported, "Customers", "Customers.Specs", null, ids);
        var generated = CSharpModelWriter.Write(imported, "Customers", specs.Additions, perChapter: true, ids);

        // The command carries the key, now as the identity type, and the spec declares it as one
        stubs(generated).ShouldContain("public record RegisterCustomer(CustomerId Id, string Name);");
        code(specs).ShouldContain("""var theCustomer = new CustomerId("customer-1");""");
    }

    [Fact]
    public void a_command_against_two_streams_types_both_and_the_spec_declares_both()
    {
        var board = EmlangReader.Read(Boards.Shelter);
        var imported = EmlangImport.ToCurated(board, "Shelter", "Shelter").Model;
        var ids = StrongTypedIds.For(imported, CSharpModelWriter.StubNames(imported));
        var specs = EmlangSpecWriter.Write(board, imported, "Shelter", "Shelter.Specs", null, ids);
        var generated = CSharpModelWriter.Write(imported, "Shelter", specs.Additions, perChapter: true, ids);

        stubs(generated).ShouldContain(
            "public record AcceptHomeCheckAssignment(HomeCheckId HomeCheckId, VolunteerApplicationId VolunteerApplicationId);");

        var all = code(specs);
        all.ShouldContain("var theHomeCheck = new HomeCheckId(Guid.CreateVersion7());");
        all.ShouldContain(".With(x => x.VolunteerApplicationId, theVolunteerApplication)");
    }
}
