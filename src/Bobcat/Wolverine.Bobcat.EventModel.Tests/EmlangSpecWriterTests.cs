using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>bobcat#423: an emlang model's examples as WolverineFx.Bobcat specifications.</summary>
public class EmlangSpecWriterTests
{
    private const string Kitchen =
        """
        slices:
          Add item:
            steps:
              - t: Website / Menu
              - c: Add item to order
              - e: Order / Item added
              - v: Order summary
            tests:
              Add to an existing order:
                given:
                  - e: Order / Order started
                    props:
                      order id: order-123
                when:
                  - c: Add item to order
                    props:
                      order id: order-123
                      item: margherita
                then:
                  - e: Order / Item added
                    props:
                      order id: order-123
                      item: margherita
                  - v: Order summary
                    props:
                      order id: order-123
                      items:
                        - margherita/9.99
                      total: 9.99
              Add to a closed order:
                given:
                  - e: Order / Order closed
                    props:
                      order id: order-9
                when:
                  - c: Add item to order
                    props:
                      order id: order-9
                then:
                  - x: Order is closed
          View summary:
            steps:
              - e: Order / Item added
              - v: Order summary
            tests:
              Nothing ordered yet:
                given:
                then:
        """;

    private static (GeneratedSpecs Specs, CSharpModelWriter.Output Stubs) generate(string yaml)
    {
        var board = EmlangReader.Read(yaml);
        var model = EmlangImport.ToCurated(board, "Kitchen").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Kitchen");
        return (specs, CSharpModelWriter.Write(model, "Kitchen", specs.Additions));
    }

    private static string code(string yaml) => generate(yaml).Specs.AllCode();

    [Fact]
    public void each_slice_is_a_feature_class_and_each_test_a_fact()
    {
        var (specs, _) = generate(Kitchen);

        specs.Features.ShouldBe(2);
        specs.Specs.ShouldBe(3);
        specs.AllCode().ShouldContain("[BobcatFeature(\"AddItemToOrder\")]");
        specs.AllCode().ShouldContain("public class add_item_to_order(AppFixture app) : KitchenSpec(app)");
        specs.AllCode().ShouldContain("public async Task add_to_an_existing_order()");
    }

    [Fact]
    public void an_identity_is_one_declared_guid_local_named_for_what_it_identifies()
    {
        var code = EmlangSpecWriterTests.code(Kitchen);

        code.ShouldContain("var theOrder = Guid.CreateVersion7(); // \"order-123\" in the model");

        // `Order / Order started` is a swimlane, not a declared stream (bobcat#439); the Order
        // aggregate is inferred from what the events are about (bobcat#444)
        code.ShouldContain("await GivenEvents<Order>(theOrder, Specify<OrderStarted>().With(x => x.OrderId, theOrder));");
    }

    [Fact]
    public void the_act_and_the_expectations_are_partial_objects_of_exactly_what_the_example_names()
    {
        var code = EmlangSpecWriterTests.code(Kitchen);

        // The command already names its stream's identity, OrderId for the inferred Order, so it
        // gets no second Id (bobcat#438, bobcat#444)
        code.ShouldContain("await WhenReceived(Specify<AddItemToOrder>().With(x => x.OrderId, theOrder).With(x => x.Item, \"margherita\"));");
        code.ShouldNotContain("x => x.Id, theOrder");
        code.ShouldContain("ThenEvents(Specify<ItemAdded>().With(x => x.OrderId, theOrder).With(x => x.Item, \"margherita\"));");
    }

    [Fact]
    public void a_view_is_checked_on_the_document_its_identity_names()
    {
        // Past 120 columns, the chain puts each member on its own line
        unindented(code(Kitchen)).ShouldContain(unindented("""
            await ThenReadModel<OrderSummary>(theOrder, Specify<OrderSummary>()
                .With(x => x.OrderId, theOrder)
                .With(x => x.Items, new List<string> { "margherita/9.99" })
                .With(x => x.Total, 9.99m));
            """));
    }

    [Fact]
    public void a_refusal_appends_nothing()
    {
        var code = EmlangSpecWriterTests.code(Kitchen);

        code.ShouldContain("ThenRefusedWith(\"Order is closed\");\n        ThenNoEvents();");
    }

    [Fact]
    public void a_view_test_that_expects_nothing_and_names_no_identity_is_no_read_model_at_all()
    {
        generate(Kitchen).Specs.AllCode().ShouldContain("await ThenNoReadModel<OrderSummary>();");
    }

    [Fact]
    public void the_stubs_carry_what_the_specs_need_streams_document_ids_and_events_only_a_given_names()
    {
        var stubs = generate(Kitchen).Stubs.AllStubs();

        // The stream is the inferred Order aggregate, a class (bobcat#444), not the swimlane (bobcat#439)
        stubs.ShouldContain("public class Order { public Guid Id { get; set; } }");
        stubs.ShouldContain("public record OrderSummary(Guid Id, Guid OrderId, List<string> Items, decimal Total);");

        // Named only by a test's given, never by a slice's steps
        stubs.ShouldContain("public record OrderClosed(Guid OrderId);");
    }

    [Fact]
    public void an_empty_given_with_an_act_names_the_declared_stream_the_act_starts()
    {
        var board = EventModelersJsonReader.Read(
            """
            { "slices": [ { "title": "Open", "aggregates": ["Cart"],
                "commands": [ { "title": "Open Cart", "aggregate": "Cart", "fields": [ { "name": "aggregateId", "type": "UUID" } ] } ],
                "events": [ { "title": "Cart Opened", "aggregate": "Cart", "fields": [ { "name": "aggregateId", "type": "UUID" } ] } ],
                "specifications": [ { "title": "Opens",
                  "given": [],
                  "when": [ { "title": "Open Cart", "type": "COMMAND", "fields": [] } ],
                  "then": [ { "title": "Cart Opened", "type": "EVENT", "fields": [] } ] } ] } ] }
            """);
        var model = EmlangImport.ToCurated(board, "Carts").Model;

        EmlangSpecWriter.Write(board, model, "Carts").AllCode().ShouldContain("await GivenNoEventsFor<Cart>(theCart);");
    }

    [Fact]
    public void an_empty_given_with_no_declared_stream_arranges_nothing()
    {
        // A swimlane is not a stream (bobcat#439), so there is no stream type to name as empty
        code(
            """
            slices:
              Start order:
                tests:
                  First item:
                    when:
                      - c: Start order
                        props:
                          order id: o-1
                    then:
                      - e: Order / Order started
                        props:
                          order id: o-1
            """).ShouldNotContain("GivenNoEventsFor");
    }

    [Fact]
    public void a_declared_type_shapes_the_literal()
    {
        var code = EmlangSpecWriterTests.code(
            """
            slices:
              Book:
                steps:
                  - c: Book session
                    props:
                      starts: datetime
                      seats: int
                      paid: boolean
                tests:
                  Booked:
                    when:
                      - c: Book session
                        props:
                          starts: "2026-08-10T09:00:00Z"
                          seats: 2
                          paid: true
            """);

        code.ShouldContain(".With(x => x.Starts, DateTimeOffset.Parse(\"2026-08-10T09:00:00Z\"))");
        code.ShouldContain(".With(x => x.Seats, 2)");
        code.ShouldContain(".With(x => x.Paid, true)");
    }

    [Fact]
    public void a_value_that_does_not_fit_its_declared_type_still_compiles_and_says_so()
    {
        EmlangSpecWriter.Literal("int", "lots").ShouldBe("default /* lots in the model */");
        EmlangSpecWriter.Literal("string", "say \"hi\"").ShouldBe("\"say \\\"hi\\\"\"");
    }

    [Fact]
    public void names_are_snake_case_and_a_single_word_feature_avoids_a_reserved_lowercase_name()
    {
        EmlangSpecWriter.FeatureClassName("Add item").ShouldBe("add_item");
        EmlangSpecWriter.FeatureClassName("Checkout").ShouldBe("checkout_slice");
        EmlangSpecWriter.MethodName("RejectsOverlappingWindow").ShouldBe("rejects_overlapping_window");
    }

    [Fact]
    public void a_second_run_reports_the_examples_with_no_specification()
    {
        var model = EmlangImport.ToCurated(EmlangReader.Read(Kitchen), "Kitchen").Model;
        var written =
            """
            [BobcatFeature("AddItemToOrder")]
            public class add_item_to_order(AppFixture app) : KitchenSpec(app)
            {
                [Fact]
                public async Task add_to_an_existing_order() { }
            }
            """;

        EmlangSpecWriter.MissingSpecs(model, [written])
            .ShouldBe(["AddItemToOrder / add to a closed order", "OrderSummary / nothing ordered yet"]);
    }

    [Fact]
    public void a_view_slice_whose_example_shares_its_name_still_compiles()
    {
        // K9CRUSH's VolunteerApplicationsQueue: the view slice and its one example have one name, so
        // class and method were both volunteer_applications_queue (CS0542). The class gives way.
        var code = EmlangSpecWriterTests.code(
            """
            slices:
              Queue:
                steps:
                  - e: Volunteer / Application submitted
                  - v: Applications queue
                tests:
                  ApplicationsQueue:
                    given:
                      - e: Volunteer / Application submitted
                    then:
                      - v: Applications queue
            """);

        code.ShouldContain("[BobcatFeature(\"ApplicationsQueue\")]");
        code.ShouldContain("public class applications_queue_feature(AppFixture app)");
        code.ShouldContain("public async Task applications_queue()");
    }

    [Fact]
    public void the_definition_links_exactly_the_identities_the_generated_specs_report()
    {
        // bobcat#435: the features were the board's chapters and the definition linked the board's
        // spelling of each test, so not one generated specification joined its slice. A projected
        // test reports {feature}/{method name read as a sentence}; that is what has to be linked.
        var (specs, output) = generate(Kitchen);

        var reported = new List<string>();
        string? feature = null;
        foreach (var line in specs.AllCode().Split('\n'))
        {
            if (System.Text.RegularExpressions.Regex.Match(line, @"\[BobcatFeature\(""([^""]*)""\)\]") is { Success: true } f)
                feature = f.Groups[1].Value;
            else if (System.Text.RegularExpressions.Regex.Match(line, @"public async Task (\w+)\(") is { Success: true } m)
                reported.Add($"{feature}/{ProjectedSpecNaming.ScenarioTitleFor(m.Groups[1].Value)}");
        }

        var linked = System.Text.RegularExpressions.Regex.Matches(output.Definition, @"LinksToSpecification\(""([^""]*)""\)")
            .Select(x => x.Groups[1].Value);

        reported.ShouldBe(
            ["AddItemToOrder/add to an existing order", "AddItemToOrder/add to a closed order", "OrderSummary/nothing ordered yet"],
            ignoreOrder: true);
        linked.ShouldBe(reported, ignoreOrder: true);
    }

    [Fact]
    public void no_generated_line_ends_in_whitespace()
    {
        code(Kitchen).Split('\n').Where(x => x.Length > 0 && char.IsWhiteSpace(x[^1])).ShouldBeEmpty();
    }

    [Fact]
    public void a_view_given_is_stored_directly_keyed_by_the_identity_the_test_names()
    {
        var code = EmlangSpecWriterTests.code(
            """
            slices:
              Assign driver:
                tests:
                  Driver assigned:
                    given:
                      - v: Available drivers
                        props:
                          drivers:
                            - driver-456
                    when:
                      - c: Assign driver
                        props:
                          order id: order-123
                    then:
                      - e: Delivery / Driver assigned
                        props:
                          order id: order-123
            """);

        unindented(code).ShouldContain(unindented("""
            await GivenReadModel<AvailableDrivers>(Specify<AvailableDrivers>()
                .With(x => x.Id, theOrder)
                .With(x => x.Drivers, new List<string> { "driver-456" }));
            """));
        code.ShouldNotContain("TODO: the model arranges");
    }

    [Fact]
    public void a_view_with_no_identity_anywhere_is_checked_as_the_only_one_of_its_type()
    {
        var (specs, _) = generate(
            """
            slices:
              Track available drivers:
                steps:
                  - v: Available drivers
                tests:
                  All available:
                    then:
                      - v: Available drivers
                        props:
                          drivers:
                            - driver-456
            """);

        specs.AllCode().ShouldContain("await ThenSingleReadModel<AvailableDrivers>(Specify<AvailableDrivers>()"
                                 + ".With(x => x.Drivers, new List<string> { \"driver-456\" }));");
        specs.Report.ShouldContain(x => x.Contains("checked as the only AvailableDrivers"));

        // The spec says so as well, so the assumption is never silent (bobcat#450)
        specs.AllCode().ShouldContain("// The example names no AvailableDrivers identity, so this checks the only one.");
    }

    [Fact]
    public void a_refusal_names_the_values_the_model_gives_it()
    {
        code(
            """
            slices:
              Register:
                tests:
                  Email taken:
                    when:
                      - c: Register
                        props:
                          email: joe@example.com
                    then:
                      - x: Email already in use
                        props:
                          email: joe@example.com
            """).ShouldContain("ThenRefusedWith(\"Email already in use\", \"joe@example.com\");");
    }

    [Fact]
    public void a_refusal_naming_an_identity_names_its_local()
    {
        code(
            """
            slices:
              Close:
                tests:
                  Already closed:
                    when:
                      - c: Close order
                        props:
                          order id: order-9
                    then:
                      - x: Order already closed
                        props:
                          order id: order-9
            """).ShouldContain("ThenRefusedWith(\"Order already closed\", theOrder);");
    }

    [Fact]
    public void an_automation_slice_says_what_triggers_it()
    {
        code(
            """
            slices:
              Send receipt:
                steps:
                  - e: Order / Payment confirmed
                  - c: Send receipt
                  - e: Receipt sent
                tests:
                  Sent:
                    when:
                      - c: Send receipt
                    then:
                      - e: Receipt sent
            """).ShouldContain("// SendReceipt is an automation, triggered by \"Payment confirmed\"\n[BobcatFeature(\"SendReceipt\")]");
    }

    [Fact]
    public void a_field_only_an_unattached_example_names_is_still_on_the_stub()
    {
        // A slice with no steps segments into nothing, so its tests attach to no slice and their
        // props never become hints: the specs name the fields anyway, so the stubs must have them
        var stubs = generate(
            """
            slices:
              Assign driver:
                tests:
                  Driver assigned:
                    when:
                      - c: Assign driver
                        props:
                          order id: order-123
                          vehicle: van
                          seats: 2
            """).Stubs.AllStubs();

        stubs.ShouldContain("public record AssignDriver(Guid OrderId, string Vehicle, int Seats);");
    }

    [Fact]
    public void the_fixture_collection_and_base_class_are_in_test_support_and_nowhere_else()
    {
        // bobcat#440: starting the real host is an edit to one file that holds nothing else
        var (specs, _) = generate(Kitchen);

        var support = specs.Files.Single(x => x.Path == EmlangSpecWriter.TestSupportFile).Content;
        support.ShouldContain("namespace Kitchen.Specs;");
        support.ShouldContain("public class AppFixture : IAsyncLifetime");
        support.ShouldContain("public class KitchenCollection : ICollectionFixture<AppFixture>;");
        support.ShouldContain("public abstract class KitchenSpec(AppFixture app) : WolverineSpec(app.Host), IAsyncLifetime");
        support.ShouldContain("public async ValueTask InitializeAsync() => await ResetAsync();");

        // The shape of the real host, for each store, in the placeholder's comments
        support.ShouldContain("MartenDaemonModeIsSolo");
        support.ShouldContain("Polecat");
        support.ShouldContain("Fisher");

        specs.Files.Where(x => x.Path != EmlangSpecWriter.TestSupportFile)
            .ShouldAllBe(x => !x.Content.Contains("class AppFixture"));
    }

    [Fact]
    public void each_slice_has_a_spec_file_in_its_chapters_folder_and_namespace()
    {
        var board = EmlangReader.Read(Kitchen);
        var model = EmlangImport.ToCurated(board, "Kitchen").Model;
        var specs = EmlangSpecWriter.Write(board, model, "Kitchen", "Kitchen.Specs");

        // Slices take the board's chapter, which is the top-level slices: key
        var add = specs.Files.Single(x => x.Path == "AddItem/AddItemToOrder.cs");
        add.Content.ShouldContain("namespace Kitchen.Specs.AddItem;");
        add.Content.ShouldContain("[BobcatFeature(\"AddItemToOrder\")]");

        // bobcat#449: the slice by the type that bears its name, for navigation and the spec manifest
        add.Content.ShouldContain("[BobcatSlice(SliceType = typeof(AddItemToOrder))]");

        // The stubs' namespaces, root and every chapter, come in once, in GlobalUsings.cs, so a
        // specification file is just its header, its namespace and its specifications
        add.Content.ShouldNotContain("using ");
        add.Content.ShouldStartWith("// Generated by `write-specs` (WolverineFx.Bobcat.EventModel) from the model's examples. Nothing regenerates it.\n\nnamespace Kitchen.Specs.AddItem;");

        var usings = specs.Files.Single(x => x.Path == EmlangSpecWriter.GlobalUsingsFile).Content;
        usings.ShouldContain("global using Kitchen;");
        usings.ShouldContain("global using Kitchen.AddItem;");
        usings.ShouldContain("global using Wolverine.Bobcat;");
        usings.ShouldNotContain("System.Collections.Generic");
        specs.Files.Count.ShouldBe(4);
    }

    [Fact]
    public void test_support_starts_the_applications_own_host_through_alba()
    {
        var board = EmlangReader.Read(Kitchen);
        var model = EmlangImport.ToCurated(board, "Kitchen").Model;
        string support(string? store) => EmlangSpecWriter.Write(board, model, "Kitchen", store: store)
            .Files.Single(x => x.Path == EmlangSpecWriter.TestSupportFile).Content;

        var plain = support(null);
        plain.ShouldContain("public IAlbaHost Host { get; private set; } = null!;");
        plain.ShouldContain("=> Host = await AlbaHost.For<Program>(x => x.ConfigureServices(services =>");
        plain.ShouldContain("services.RunWolverineInSoloMode();");
        plain.ShouldContain("opts.ApplicationAssembly = typeof(Program).Assembly");
        plain.ShouldNotContain("TODO");
        plain.ShouldNotContain("placeholder");

        // No store named: each store's way of running its daemon, as a comment, so it still compiles
        plain.ShouldContain("//   Marten:  services.MartenDaemonModeIsSolo();");
        plain.ShouldNotContain("using Marten;");

        var marten = support("marten");
        marten.ShouldContain("using Marten;");
        marten.ShouldContain("        services.MartenDaemonModeIsSolo();");

        support("fisher").ShouldContain("// Fisher's AddAsyncDaemon() already runs in solo mode");
        Should.Throw<ArgumentOutOfRangeException>(() => support("raven"));
    }

    private const string Clinic =
        """
        slices:
          Book visit:
            steps:
              - c: Book visit
              - e: Visit / Visit booked
            tests:
              Booked:
                when:
                  - c: Book visit
                    props:
                      visit id: visit-1
                      vet: Dr. Hollis
                      room: 3
                      notes: ""
                then:
                  - e: Visit / Visit booked
                    props:
                      visit id: visit-1
                      vet: Dr. Hollis
                      room: 3
                      notes: ""
              Short:
                when:
                  - c: Book visit
                    props:
                      visit id: visit-2
                      vet: Dr. Hollis
                then:
                  - e: Visit / Visit booked
                    props:
                      visit id: visit-2
                      vet: Dr. Hollis
        """;

    // Each line without its indentation, so an expectation need not know how deep the generated code sits
    internal static string unindented(string text)
        => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimStart()));

    [Fact]
    public void an_object_with_more_than_three_members_is_a_property_value_table()
    {
        var code = unindented(generate(Clinic).Specs.AllCode());

        code.ShouldContain(unindented(""""
                           ThenEvents(Specify<VisitBooked>($$"""
                               | Property | Value        |
                               | VisitId  | {{theVisit}} |
                               | Vet      | Dr. Hollis   |
                               | Room     | 3            |
                               | Notes    | EMPTY        |
                               """));
                           """"));

        // Three or fewer members stay a With chain
        code.ShouldContain("ThenEvents(Specify<VisitBooked>().With(x => x.VisitId, theVisit).With(x => x.Vet, \"Dr. Hollis\"));");
    }

    [Fact]
    public void a_chain_that_would_run_past_120_columns_puts_each_member_on_its_own_line()
    {
        var code = generate("""
            slices:
              Accept assignment:
                steps:
                  - c: Accept home check assignment
                  - e: Home check / Home check assignment accepted
                tests:
                  Accepted:
                    when:
                      - c: Accept home check assignment
                        props:
                          home check id: check-1
                          volunteer application id: application-1
                    then:
                      - e: Home check / Home check assignment accepted
            """).Specs.AllCode();

        var lines = code.Replace("\r\n", "\n").Split('\n');
        lines.Where(x => x.Length > 120).ShouldBeEmpty();

        unindented(code).ShouldContain(unindented("""
            await WhenReceived(Specify<AcceptHomeCheckAssignment>()
                .With(x => x.HomeCheckId, theHomeCheck)
                .With(x => x.VolunteerApplicationId, theVolunteerApplication));
            """));
    }

    [Fact]
    public void a_value_with_no_faithful_cell_keeps_the_chain_one_member_to_a_line()
    {
        var code = unindented(generate(Clinic.Replace("vet: Dr. Hollis", "vet: \"Dr. | Hollis\"")).Specs.AllCode());

        code.ShouldContain(unindented("""
                           Specify<VisitBooked>()
                               .With(x => x.VisitId, theVisit)
                               .With(x => x.Vet, "Dr. | Hollis")
                           """));
    }

    [Fact]
    public void the_spec_namespace_defaults_to_the_application_namespace_dot_specs()
    {
        var (specs, _) = generate(Kitchen);
        specs.Files.Single(x => x.Path == EmlangSpecWriter.TestSupportFile).Content.ShouldContain("namespace Kitchen.Specs;");
    }

    [Fact]
    public void every_minted_id_is_a_version_7_guid()
    {
        // A random v4 Guid fragments a stream table's index, and generated code is copied
        generate(Kitchen).Specs.AllCode().ShouldNotContain("Guid.NewGuid()");
    }
}
