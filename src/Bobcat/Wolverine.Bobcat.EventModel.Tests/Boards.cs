namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// The boards Bobcat.EventModel's own tests import, copied here with the spec writer's tests (GH-5082) because the
/// specification half of those tests moved with the writer. Same text, so the two halves are tested on one board.
/// </summary>
internal static class Boards
{
    /// <summary>The Shelter board Bobcat's AggregateInferenceTests imports: two chapters, streams inferred from the examples.</summary>
    internal const string Shelter =
        """
        slices:
          Volunteering:
            steps:
              - c: Volunteer / Apply to volunteer
              - e: Volunteer / Volunteer application submitted
              - c: Admin / Review volunteer application
              - e: Admin / Volunteer application reviewed
              - c: Admin / Approve volunteer
              - e: Admin / Volunteer approved
              - c: Admin / Reject volunteer application
              - e: Admin / Volunteer application rejected
              - c: Staff / Request home check
              - e: Staff / Home check requested
              - c: Volunteer / Accept home check assignment
              - e: Volunteer / Home check assignment accepted
            tests:
              Submitted:
                when:
                  - c: Apply to volunteer
                then:
                  - e: Volunteer application submitted
              Reviewed:
                given:
                  - e: Volunteer application submitted
                when:
                  - c: Review volunteer application
                then:
                  - e: Volunteer application reviewed
              Approved:
                given:
                  - e: Volunteer application reviewed
                when:
                  - c: Approve volunteer
                then:
                  - e: Volunteer approved
              Rejected:
                given:
                  - e: Volunteer application reviewed
                when:
                  - c: Reject volunteer application
                then:
                  - e: Volunteer application rejected
              Requested:
                given:
                  - e: Application reviewed
                when:
                  - c: Request home check
                then:
                  - e: Home check requested
              Accepted:
                given:
                  - e: Home check requested
                  - e: Volunteer approved
                when:
                  - c: Accept home check assignment
                then:
                  - e: Home check assignment accepted
          Booking:
            steps:
              - c: System / Propose home check appointment
              - e: System / Home check appointment proposed
              - c: System / Propose foster handover appointment
              - e: System / Foster handover appointment proposed
              - c: Member / Confirm appointment
              - e: Member / Appointment confirmed
              - c: Staff / Cancel appointment
              - e: Staff / Appointment cancelled
            tests:
              Home check proposed:
                when:
                  - c: Propose home check appointment
                then:
                  - e: Home check appointment proposed
              Foster proposed:
                when:
                  - c: Propose foster handover appointment
                then:
                  - e: Foster handover appointment proposed
              Confirmed:
                given:
                  - e: Home check appointment proposed
                when:
                  - c: Confirm appointment
                then:
                  - e: Appointment confirmed
              Cancelled:
                given:
                  - e: Appointment confirmed
                when:
                  - c: Cancel appointment
                then:
                  - e: Appointment cancelled
        """;

    /// <summary>A command that starts a stream whose handler mints the id (StreamStartIdentityTests).</summary>
    internal const string MintedIdentity =
        """
        { "slices": [ { "title": "Apply", "aggregates": ["Application"],
            "commands": [ { "title": "Submit Application", "aggregate": "Application", "fields": [
              { "name": "areas", "type": "String" } ] } ],
            "events": [ { "title": "Application Submitted", "aggregate": "Application", "fields": [
              { "name": "applicationId", "type": "UUID", "idAttribute": true },
              { "name": "areas", "type": "String" } ] } ],
            "specifications": [ { "title": "Submits",
              "given": [],
              "when": [ { "title": "Submit Application", "type": "COMMAND", "fields": [ { "name": "areas", "example": "dogs" } ] } ],
              "then": [ { "title": "Application Submitted", "type": "EVENT", "fields": [ { "name": "areas", "example": "dogs" } ] } ] } ] } ] }
        """;

    /// <summary>A string-keyed stream, started with the id its command carries (StreamStartIdentityTests).</summary>
    internal const string StreamStartNaturalKey =
        """
        { "slices": [ { "title": "Register", "aggregates": ["Customer"],
            "commands": [ { "title": "Register Customer", "aggregate": "Customer", "fields": [
              { "name": "name", "type": "String" } ] } ],
            "events": [ { "title": "Customer Registered", "aggregate": "Customer", "fields": [
              { "name": "email", "type": "String", "idAttribute": true },
              { "name": "name", "type": "String" } ] } ],
            "specifications": [ { "title": "Registers",
              "given": [],
              "when": [ { "title": "Register Customer", "type": "COMMAND", "fields": [ { "name": "name", "example": "Ann" } ] } ],
              "then": [ { "title": "Customer Registered", "type": "EVENT", "fields": [ { "name": "name", "example": "Ann" } ] } ] } ] } ] }
        """;

    /// <summary>The Shop board StrongTypedIdentityTests imports.</summary>
    internal const string Ordering =
        """
        slices:
          Ordering:
            steps:
              - c: Member/Place Order
                props: { orderId: order-123, total: 12.5 }
              - e: Member/Order Placed
                props: { orderId: order-123 }
              - c: Member/Cancel Order
                props: { orderId: order-123 }
              - e: Member/Order Cancelled
              - v: Member/Order Summary
            tests:
              AnOrderIsCancelled:
                given: [{ e: Member/Order Placed }]
                when: [{ c: Member/Cancel Order }]
                then: [{ e: Member/Order Cancelled }]
        """;

    /// <summary>A natural key under --strong-typed-ids (StrongTypedIdentityTests).</summary>
    internal const string StrongTypedNaturalKey =
        """
        { "slices": [ { "title": "Register", "aggregates": ["Customer"],
            "commands": [ { "title": "Register Customer", "aggregate": "Customer", "fields": [
              { "name": "name", "type": "String" } ] } ],
            "events": [ { "title": "Customer Registered", "aggregate": "Customer", "fields": [
              { "name": "email", "type": "String", "idAttribute": true },
              { "name": "name", "type": "String" } ] } ],
            "specifications": [ { "title": "Registers",
              "given": [],
              "when": [ { "title": "Register Customer", "type": "COMMAND", "fields": [ { "name": "name", "example": "Ann" } ] } ],
              "then": [ { "title": "Customer Registered", "type": "EVENT", "fields": [ { "name": "name", "example": "Ann" } ] } ] } ] } ] }
        """;

    /// <summary>An eventmodelers.ai config.json export (EventModelersJsonReaderTests).</summary>
    internal const string Config =
        """
        {
          "context": "Shop",
          "slices": [
            {
              "title": "Add Item",
              "sliceType": "STATE_CHANGE",
              "aggregates": ["Cart"],
              "screens": [ { "title": "Cart Page", "fields": [] } ],
              "commands": [
                { "title": "Add Item", "fields": [
                  { "name": "cartId", "type": "UUID", "cardinality": "Single", "idAttribute": true },
                  { "name": "quantity", "type": "Int", "cardinality": "Single" },
                  { "name": "tags", "type": "String", "cardinality": "List" } ] }
              ],
              "events": [
                { "title": "Item Added", "aggregate": "default", "fields": [
                  { "name": "cartId", "type": "UUID", "cardinality": "Single" } ] }
              ],
              "readmodels": [],
              "processors": [],
              "specifications": [
                {
                  "title": "Adds to an empty cart",
                  "given": [],
                  "when": [ { "title": "Add Item", "type": "SPEC_COMMAND",
                              "fields": [ { "name": "cartId", "example": "cart-1" }, { "name": "quantity", "example": "2" }, { "name": "tags" } ] } ],
                  "then": [ { "title": "Item Added", "type": "SPEC_EVENT",
                              "fields": [ { "name": "cartId", "example": "cart-1" } ] } ]
                },
                {
                  "title": "Too many",
                  "given": [],
                  "when": [ { "title": "Add Item", "type": "COMMAND", "fields": [ { "name": "quantity", "example": "99" } ] } ],
                  "then": { "title": "Error-Case", "type": "SPEC_ERROR", "description": "Quantity over limit" }
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// An EM Spec <c>config.json</c> in the shapes the platform's API reference and schema describe, after Bobcat's own
    /// reader fixture (bobcat#846, <c>EventModelersJsonReaderTests.Rich</c>): a command with an <c>apiEndpoint</c>, a
    /// generated and a tagged field, a linked copy of the event in a second slice, a list read model, scenarios with
    /// <c>context</c>, an inline <c>QUERY</c>, <c>examples</c> rows, <c>expectEmptyList</c> and a <c>contextNote</c>,
    /// slice status (Blocked, Informational), description and actors, and an automation slice (GH-6004).
    /// </summary>
    internal const string OrdersSpec =
        """
        {
          "slices": [
            {
              "title": "Place Order",
              "chapter": "Ordering",
              "sliceType": "STATE_CHANGE",
              "status": "Blocked",
              "description": "An order needs at least one line.\nOrders over 1000 need approval.",
              "actors": [ { "name": "Customer", "authRequired": true, "rolesRequired": ["buyer"] } ],
              "aggregates": ["Order"],
              "commands": [
                { "id": "cmd-1", "linkedId": "cmd-1", "elementCopy": false, "title": "Place Order", "type": "COMMAND",
                  "apiEndpoint": "POST /orders/{orderId}", "aggregate": "default",
                  "fields": [
                    { "name": "orderId", "type": "UUID", "idAttribute": true },
                    { "name": "total", "type": "Number" } ] }
              ],
              "events": [
                { "id": "evt-1", "linkedId": "evt-1", "elementCopy": false, "title": "Order Placed", "type": "EVENT", "aggregate": "Order",
                  "fields": [
                    { "name": "orderId", "type": "UUID", "idAttribute": true, "tag": true },
                    { "name": "placedAt", "type": "DateTime", "generated": true },
                    { "name": "total", "type": "Decimal" } ] }
              ],
              "readmodels": [],
              "screens": [],
              "processors": [],
              "specifications": [
                {
                  "id": "spec-1",
                  "title": "Places an order over the approval limit",
                  "contextNote": "Approval is a manual step outside the system.",
                  "context": [ { "id": "rm-limits", "title": "Approval Limits", "type": "SPEC_READMODEL",
                                 "fields": [ { "name": "limit", "example": "1000" } ] } ],
                  "given": [],
                  "when": [ { "id": "cmd-1", "title": "Place Order", "type": "SPEC_COMMAND",
                              "fields": [ { "name": "orderId", "example": "o-1" }, { "name": "total", "example": "1500" } ] } ],
                  "then": [ { "id": "evt-copy", "title": "Order Placed (copy)", "type": "SPEC_EVENT",
                              "fields": [ { "name": "orderId", "example": "o-1" }, { "name": "total", "example": "1500" } ] } ]
                },
                {
                  "id": "spec-1b",
                  "title": "Refuses an empty order",
                  "given": [],
                  "when": [ { "id": "cmd-1", "title": "Place Order", "type": "SPEC_COMMAND",
                              "fields": [ { "name": "orderId", "example": "o-2" }, { "name": "total", "example": "0" } ] } ],
                  "then": [],
                  "expectError": true,
                  "errorDescription": "An order needs at least one line"
                }
              ]
            },
            {
              "title": "Open Orders",
              "chapter": "Ordering",
              "sliceType": "STATE_VIEW",
              "status": "Done",
              "aggregates": [],
              "commands": [],
              "events": [
                { "id": "evt-copy", "linkedId": "evt-1", "elementCopy": true, "title": "Order Placed (copy)", "type": "EVENT", "aggregate": "default", "fields": [] }
              ],
              "readmodels": [
                { "id": "rm-1", "linkedId": "rm-1", "elementCopy": false, "title": "Open Orders", "type": "READMODEL", "listElement": true,
                  "fields": [ { "name": "orderId", "type": "UUID" }, { "name": "total", "type": "Decimal" } ] }
              ],
              "screens": [], "processors": [],
              "specifications": [
                {
                  "id": "spec-2",
                  "title": "Lists the open orders",
                  "given": [ { "id": "evt-copy", "title": "Order Placed", "type": "SPEC_EVENT", "fields": [ { "name": "orderId", "example": "o-1" } ] } ],
                  "when": [],
                  "then": [ { "id": "rm-1", "title": "Open Orders", "type": "SPEC_READMODEL" } ],
                  "examples": [ { "orderId": "o-1", "total": 1500 } ]
                },
                {
                  "id": "spec-3",
                  "title": "No open orders at the start",
                  "given": [],
                  "when": [],
                  "then": [ { "id": "rm-1", "title": "Open Orders", "type": "SPEC_READMODEL" } ],
                  "expectEmptyList": true,
                  "examples": []
                },
                {
                  "id": "spec-4",
                  "title": "Asked for one customer",
                  "given": [ { "id": "evt-copy", "title": "Order Placed", "type": "SPEC_EVENT" } ],
                  "when": [ { "id": "rm-1", "title": "Open Orders", "type": "QUERY", "fields": [ { "name": "customerId", "example": "c-9" } ] } ],
                  "then": [ { "id": "rm-1", "title": "Open Orders", "type": "SPEC_READMODEL", "fields": [ { "name": "total", "example": "1500" } ] } ]
                }
              ]
            },
            {
              "title": "Send Confirmation",
              "chapter": "Ordering",
              "sliceType": "AUTOMATION",
              "status": "Informational",
              "description": "The confirmation is a courtesy; the order stands without it.",
              "aggregates": ["Order"],
              "processors": [ { "id": "proc-1", "title": "Order Placed Processor", "type": "PROCESSOR" } ],
              "commands": [
                { "id": "cmd-2", "linkedId": "cmd-2", "elementCopy": false, "title": "Send Confirmation", "type": "COMMAND", "aggregate": "default",
                  "fields": [ { "name": "orderId", "type": "UUID", "idAttribute": true } ] }
              ],
              "events": [
                { "id": "evt-2", "linkedId": "evt-2", "elementCopy": false, "title": "Confirmation Sent", "type": "EVENT", "aggregate": "Order",
                  "fields": [ { "name": "orderId", "type": "UUID", "idAttribute": true } ] }
              ],
              "readmodels": [], "screens": [],
              "specifications": [
                {
                  "id": "spec-5",
                  "title": "Confirms a placed order",
                  "given": [ { "id": "evt-1", "title": "Order Placed", "type": "SPEC_EVENT", "fields": [ { "name": "orderId", "example": "o-1" } ] } ],
                  "when": [ { "id": "cmd-2", "title": "Send Confirmation", "type": "SPEC_COMMAND", "fields": [ { "name": "orderId", "example": "o-1" } ] } ],
                  "then": [ { "id": "evt-2", "title": "Confirmation Sent", "type": "SPEC_EVENT", "fields": [ { "name": "orderId", "example": "o-1" } ] } ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// A dcb.events Playground model (bobcat#849), one of the nine Bobcat vendors: two commands' worth of scenarios over
    /// tagged events, a rejection and an acceptance. Read from <c>Fixtures/dcb/</c>, where its licence sits beside it.
    /// </summary>
    internal static string Dcb => File.ReadAllText(Fixture("dcb", "prevent-record-duplication.dcb.json"));

    private static string Fixture(string folder, string file, [System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", folder, file);
}
