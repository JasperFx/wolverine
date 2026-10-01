# Authentication and Authorization

Wolverine.HTTP endpoints are just routes within your ASP.Net Core application, and will happily work with all existing
ASP.Net Core middleware. Likewise, the built int `[AllowAnonymous]` and `[Authorize]` attributes from ASP.Net Core are
valid on Wolverine HTTP endpoints.

To require authorization on all endpoints (which is overridden by `[AllowAnonymous]`), use this syntax:

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.RequireAuthorizeOnAll();
});
```

or more selectively, the code above is just syntactical sugar for:

<!-- snippet: sample_requireauthorizeonall -->
<a id='snippet-sample_requireauthorizeonall'></a>
```cs
/// <summary>
/// Equivalent of calling RequireAuthorization() on all wolverine endpoints
/// </summary>
public void RequireAuthorizeOnAll()
{
    ConfigureEndpoints(e => e.RequireAuthorization());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/WolverineHttpOptions.cs#L297-L306' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_requireauthorizeonall' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Tracking User Name

Wolverine can automatically propagate the authenticated user's identity from the HTTP `ClaimsPrincipal` through the messaging infrastructure. When enabled, the `ClaimsPrincipal.Identity.Name` is:

1. Set on `IMessageContext.UserName` for the current request
2. Propagated to all outgoing message envelopes via `Envelope.UserName`
3. Automatically set on the outboxed session when using the Wolverine + Marten, Polecat or Fisher integration (`LastModifiedBy` on Marten and Polecat, `CurrentUserName` on Fisher)
4. Added as an OpenTelemetry tag (`enduser.id`) on the current activity

To enable this feature, set `EnableRelayOfUserName` in your Wolverine configuration:

```csharp
builder.Host.UseWolverine(opts =>
{
    // Automatically relay the authenticated user name from HTTP
    // through the messaging infrastructure
    opts.EnableRelayOfUserName = true;
});
```

When this option is enabled, every HTTP endpoint with a Wolverine message context relays the user onto it, before your endpoint code executes. That includes endpoints that take `IMessageContext` or `IMessageBus`, and endpoints that only open an outboxed session -- for example one that returns an `IMartenOp` or `IStartStream`, uses `[WriteAggregate]`, or commits through EF Core. Wolverine reads `HttpContext.User?.Identity?.Name`; which claim supplies it is up to your authentication setup (`NameClaimType`).

The user name is carried on outgoing envelopes, so downstream message handlers will also have access to the original user name via `IMessageContext.UserName` or `Envelope.UserName`. This is particularly useful for auditing and tracking who initiated a chain of messages.

### Marten, Polecat and Fisher Integration

When using the Wolverine + Marten integration, the user name is automatically applied to `IDocumentSession.LastModifiedBy`. This means Marten's `mt_last_modified_by` metadata column, and the `user_name` column on events, will be populated with the authenticated user's name for any documents stored or events appended during the request -- even for cascading messages downstream from the original HTTP request. Both columns are opt-in on the Marten side: enable `LastModifiedBy` metadata on the document type and `Events.MetadataConfig.UserNameEnabled` for events.

Polecat applies the user name to `IDocumentSession.LastModifiedBy` the same way, and Fisher to `IDocumentSession.CurrentUserName`. Their columns are opt-in too (`[LastModifiedByMetadata]` on the document, `Events.EnableUserName` for events).
