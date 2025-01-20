# IncidentService TODO


## "Script"

1. Show bootstrapping Marten & Wolverine
2. Show event types and aggregate type
3. Wolverine http endpoint to create an incident. Hit VSA
4. Show Alba test for the same, show how being a pure function makes unit testing easy
5. Do a command endpoint that appends events. Hit validation logic too
6. Hit event metadata, just done for you
7. Now go and make things fast



* Remove all "Now" props in event types






## Other Code

```csharp
//
// agentIncidents.MapPost("{incidentId:guid}/priority",
//     (
//             IDocumentSession documentSession,
//             Guid incidentId,
//             Guid agentId,
//             [FromIfMatchHeader] string eTag,
//             PrioritiseIncidentRequest body,
//             CancellationToken ct
//         ) =>
//         documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state, new PrioritiseIncident(incidentId, body.Priority, agentId, Now)), ct)
// );
//
// agentIncidents.MapPost("{incidentId:guid}/assign",
//     (
//             IDocumentSession documentSession,
//             Guid incidentId,
//             Guid agentId,
//             [FromIfMatchHeader] string eTag,
//             CancellationToken ct
//         ) =>
//         documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state, new AssignAgentToIncident(incidentId, agentId, Now)), ct)
// );
//
// customersIncidents.MapPost("{incidentId:guid}/responses/",
//     (
//             IDocumentSession documentSession,
//             Guid incidentId,
//             Guid customerId,
//             [FromIfMatchHeader] string eTag,
//             RecordCustomerResponseToIncidentRequest body,
//             CancellationToken ct
//         ) =>
//         documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state,
//                 new RecordCustomerResponseToIncident(incidentId,
//                     new IncidentResponse.FromCustomer(customerId, body.Content), Now)), ct)
// );
//
// agentIncidents.MapPost("{incidentId:guid}/responses/",
//     (
//         IDocumentSession documentSession,
//         [FromIfMatchHeader] string eTag,
//         Guid incidentId,
//         Guid agentId,
//         RecordAgentResponseToIncidentRequest body,
//         CancellationToken ct
//     ) =>
//     {
//         var (content, visibleToCustomer) = body;
//
//         return documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state,
//                 new RecordAgentResponseToIncident(incidentId,
//                     new IncidentResponse.FromAgent(agentId, content, visibleToCustomer), Now)), ct);
//     }
// );
//
// agentIncidents.MapPost("{incidentId:guid}/resolve",
//     (
//             IDocumentSession documentSession,
//             Guid incidentId,
//             Guid agentId,
//             [FromIfMatchHeader] string eTag,
//             ResolveIncidentRequest body,
//             CancellationToken ct
//         ) =>
//         documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state, new ResolveIncident(incidentId, body.Resolution, agentId, Now)), ct)
// );
//
// customersIncidents.MapPost("{incidentId:guid}/acknowledge",
//     (
//             IDocumentSession documentSession,
//             Guid incidentId,
//             Guid customerId,
//             [FromIfMatchHeader] string eTag,
//             CancellationToken ct
//         ) =>
//         documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state, new AcknowledgeResolution(incidentId, customerId, Now)), ct)
// );
//
// agentIncidents.MapPost("{incidentId:guid}/close",
//     async (
//         IDocumentSession documentSession,
//         Guid incidentId,
//         Guid agentId,
//         [FromIfMatchHeader] string eTag,
//         CancellationToken ct) =>
//     {
//         await documentSession.GetAndUpdate<Incident>(incidentId, ToExpectedVersion(eTag),
//             state => Handle(state, new CloseIncident(incidentId, agentId, Now)), ct);
//
//         return Ok();
//     }
// );
//
// customersIncidents.MapGet("",
//     (IQuerySession querySession, Guid customerId, [FromQuery] int? pageNumber, [FromQuery] int? pageSize,
//             CancellationToken ct) =>
//         querySession.Query<IncidentShortInfo>().Where(i => i.CustomerId == customerId)
//             .ToPagedListAsync(pageNumber ?? 1, pageSize ?? 10, ct)
// );
//
// incidents.MapGet("{incidentId:guid}",
//     (HttpContext context, IQuerySession querySession, Guid incidentId) =>
//         querySession.Json.WriteById<IncidentDetails>(incidentId, context)
// );
//
// incidents.MapGet("{incidentId:guid}/history",
//     (HttpContext context, IQuerySession querySession, Guid incidentId) =>
//         querySession.Query<IncidentHistory>().Where(i => i.IncidentId == incidentId).WriteArray(context)
// );
//
// customersIncidents.MapGet("incidents-summary",
//     (HttpContext context, IQuerySession querySession, Guid customerId) =>
//         querySession.Json.WriteById<CustomerIncidentsSummary>(customerId, context)
// );
```