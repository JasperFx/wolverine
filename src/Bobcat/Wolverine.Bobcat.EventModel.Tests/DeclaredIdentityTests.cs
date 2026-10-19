using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// The identity and stream a model declares on an element, used by a generated spec even when the
/// example repeats neither (the Underwriting case: an aggregate and an idAttribute, no values).
/// </summary>
public class DeclaredIdentityTests
{
    private static string generate(EmlangBoard board)
    {
        var model = EmlangImport.ToCurated(board, "Limits").Model;
        return EmlangSpecWriter.Write(board, model, "Limits").AllCode();
    }

    private const string ValuelessConfig =
        """
        {
          "slices": [
            {
              "title": "Grant Limit",
              "aggregates": ["AuthorityLimit"],
              "commands": [ { "title": "Grant Limit", "aggregate": "AuthorityLimit", "fields": [
                { "name": "authorityLimitId", "type": "UUID", "idAttribute": true },
                { "name": "amount", "type": "Decimal" } ] } ],
              "events": [
                { "title": "Limit Requested", "aggregate": "AuthorityLimit", "fields": [
                  { "name": "authorityLimitId", "type": "UUID", "idAttribute": true } ] },
                { "title": "Limit Granted", "aggregate": "AuthorityLimit", "fields": [
                  { "name": "authorityLimitId", "type": "UUID", "idAttribute": true },
                  { "name": "amount", "type": "Decimal" } ] } ],
              "specifications": [
                { "title": "Grants a requested limit",
                  "given": [ { "title": "Limit Requested", "type": "SPEC_EVENT", "fields": [] } ],
                  "when": [ { "title": "Grant Limit", "type": "SPEC_COMMAND", "fields": [ { "name": "amount", "example": "500" } ] } ],
                  "then": [ { "title": "Limit Granted", "type": "SPEC_EVENT", "fields": [] } ] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void an_example_that_names_no_stream_takes_the_one_its_element_declares()
    {
        generate(EventModelersJsonReader.Read(ValuelessConfig))
            .ShouldContain("await GivenEvents<AuthorityLimit>(theAuthorityLimit,");
    }

    [Fact]
    public void a_marked_identity_with_no_example_value_is_minted_once_and_set_everywhere()
    {
        var code = generate(EventModelersJsonReader.Read(ValuelessConfig));

        code.ShouldContain("var theAuthorityLimit = Guid.CreateVersion7();");
        code.ShouldContain("Specify<LimitRequested>().With(x => x.AuthorityLimitId, theAuthorityLimit)");
        EmlangSpecWriterTests.unindented(code).ShouldContain(EmlangSpecWriterTests.unindented("""
            Specify<GrantLimit>()
                .With(x => x.Amount, 500m)
                .With(x => x.AuthorityLimitId, theAuthorityLimit)
            """));
        code.ShouldContain("ThenEvents(Specify<LimitGranted>().With(x => x.AuthorityLimitId, theAuthorityLimit));");
        code.ShouldNotContain("TODO: the model names no");
    }

    [Fact]
    public void an_aggregate_id_field_is_its_streams_identity_when_nothing_is_marked()
    {
        var code = generate(EventModelersJsonReader.Read(
            """
            { "slices": [ { "title": "Clear", "aggregates": ["Cart"],
                "commands": [ { "title": "Clear Cart", "aggregate": "Cart", "fields": [ { "name": "aggregateId", "type": "UUID" } ] } ],
                "events": [ { "title": "Cart Cleared", "aggregate": "Cart", "fields": [ { "name": "aggregateId", "type": "UUID" } ] } ],
                "specifications": [ { "title": "Clears",
                  "given": [ { "title": "Cart Cleared", "type": "EVENT", "fields": [] } ],
                  "when": [ { "title": "Clear Cart", "type": "COMMAND", "fields": [] } ],
                  "then": [ { "title": "Cart Cleared", "type": "EVENT", "fields": [] } ] } ] } ] }
            """));

        code.ShouldContain("var theCart = Guid.CreateVersion7();");
        code.ShouldContain("await GivenEvents<Cart>(theCart, Specify<CartCleared>().With(x => x.AggregateId, theCart));");
    }

    [Fact]
    public void an_emlang_swimlane_is_an_actor_so_the_stream_is_named_for_the_events_subject()
    {
        // bobcat#439: K9CRUSH writes `Admin / Volunteer approved`, and reading the swimlane as the
        // stream spread one application's events over Admin, Volunteer and ShelterStaff. bobcat#444:
        // nor is the stream left without an aggregate type. It is inferred from the events, named
        // for their subject, and the act addresses it through the command's Id (bobcat#438).
        var code = generate(EmlangReader.Read(
            """
            slices:
              Place:
                steps:
                  - c: Place order
                  - e: Admin / Order placed
                    props:
                      order id: uuid
                tests:
                  Placed twice:
                    given:
                      - e: Order placed
                    when:
                      - c: Place order
                    then:
                      - x: Already placed
            """));

        code.ShouldContain("var theOrder = Guid.CreateVersion7();");
        code.ShouldContain("await GivenEvents<Order>(theOrder, Specify<OrderPlaced>().With(x => x.OrderId, theOrder));");
        code.ShouldContain("await WhenReceived(Specify<PlaceOrder>().With(x => x.Id, theOrder));");
        code.ShouldNotContain("GivenEvents<Admin>");
        code.ShouldNotContain("TODO: the model names no");
    }

    [Fact]
    public void an_example_value_for_the_identity_is_the_streams_local_for_the_elements_that_name_none()
    {
        var code = generate(EventModelersJsonReader.Read(ValuelessConfig.Replace(
            "\"given\": [ { \"title\": \"Limit Requested\", \"type\": \"SPEC_EVENT\", \"fields\": [] } ]",
            "\"given\": [ { \"title\": \"Limit Requested\", \"type\": \"SPEC_EVENT\", \"fields\": [ { \"name\": \"authorityLimitId\", \"example\": \"limit-7\" } ] } ]")));

        code.ShouldContain("var theAuthorityLimit = Guid.CreateVersion7(); // \"limit-7\" in the model");
        code.ShouldContain("ThenEvents(Specify<LimitGranted>().With(x => x.AuthorityLimitId, theAuthorityLimit));");
        code.ShouldNotContain("theAuthorityLimit2");
    }

    [Fact]
    public void a_minted_identity_nothing_uses_is_not_declared()
    {
        // A view's identity minted for a view no step reads would be an unused local: CS0219
        var code = generate(EventModelersJsonReader.Read(
            """
            { "slices": [ { "title": "Dashboard",
                "readmodels": [ { "title": "Cell Dashboard", "fields": [ { "name": "cellId", "type": "UUID", "idAttribute": true } ] } ],
                "specifications": [ { "title": "Nothing yet", "given": [], "when": [], "then": [] } ] } ] }
            """));

        code.ShouldNotContain("var theCell");
    }
}
