# Wolverine.HTTP Learns the QUERY Verb

*Draft — targeting a Wolverine 6.17 write-up. Author: Jeremy D. Miller.*

Every so often a feature lands that is almost embarrassingly small in the diff but scratches a genuine, long-standing itch. Wolverine.HTTP now speaks the [HTTP `QUERY` method (RFC 10008)](https://www.rfc-editor.org/rfc/rfc10008.html) through a single new `[WolverineQuery]` attribute — and I want to walk through what it is, why you'd reach for it, and how it behaves inside Wolverine's middleware model.

---

## What is the QUERY method, and why should I care?

If you've ever built a search endpoint, you've probably felt the tension. Search criteria want to be a **body** — nested filters, arrays of facets, date ranges, a big structured DTO. But "read-only, cacheable, idempotent" wants to be a **`GET`**. And `GET` famously does not carry a request body in any way you can rely on.

So we all compromise. Either you cram everything into an ever-growing query string (and start bumping into URL length limits and gnarly encoding), or you `POST` your search — quietly giving up the semantic promise that this call is safe and idempotent, and confusing every proxy, cache, and reader of your API along the way.

`QUERY` is the method that resolves that tension. It is **safe and idempotent — like `GET`** — but it is **allowed to carry a request body — like `POST`**. It's purpose-built for exactly the search/query endpoints whose criteria are too large or too structured to encode in a URL.

---

## The API surface: one attribute

The entire feature is a single attribute, `[WolverineQuery]`, that sits right alongside the verb attributes you already know — `[WolverineGet]`, `[WolverinePost]`, `[WolverinePut]`, and friends. There's no new fluent method to learn and no configuration to flip on.

Here's a complete search endpoint:

```csharp
using Wolverine.Http;

public record SearchRequest(string Term, int Page);
public record SearchResults(string Term, int Page, string[] Hits);

// QUERY (RFC 10008) is safe and idempotent like GET, but carries a request body — ideal for
// search endpoints whose criteria are too large or structured for the query string. Wolverine
// binds the request body just like it would for POST.
[WolverineQuery("/search")]
public static SearchResults Search(SearchRequest request)
{
    var hits = Enumerable.Range(1, request.Page)
        .Select(i => $"{request.Term}-{i}")
        .ToArray();

    return new SearchResults(request.Term, request.Page, hits);
}
```

That's it. The `SearchRequest` binds from the request body **exactly as it would for a `POST` endpoint** — same JSON deserialization, same everything. The only difference on the wire is the HTTP method, which flows straight through to ASP.NET Core as route metadata.

---

## Middleware rules are dependency-based, not verb-based

This is the part I want to be very precise about, because it's the most common wrong assumption.

You might expect a "safe" verb like `QUERY` to be automatically exempt from transactional or outbox middleware. **It is not — and that's by design.** Wolverine has never keyed its middleware decisions off the HTTP verb; it keys them off the **dependencies your handler actually takes**:

- **Outbox** middleware is applied when your handler depends on `IMessageBus` / `IMessageContext`.
- **Transactional** middleware is applied when your handler takes a persistence dependency like Marten's `IDocumentSession` or an EF Core `DbContext`.

So the `/search` endpoint above stays free of transactional middleware because it takes no persistence dependency — **not** because it's a `QUERY`. The rule cuts both ways. Take an `IDocumentSession` on a `QUERY` endpoint under `AutoApplyTransactions()` and you'll get transactional middleware wrapped around it, exactly as you would on a `POST`:

```csharp
using Marten;
using Wolverine.Http;

// Taking an IDocumentSession attracts AutoApplyTransactions on a QUERY endpoint
// exactly as it would on a POST — there is no verb-based exemption.
[WolverineQuery("/search/audited")]
public static SearchResults SearchAudited(SearchRequest request, IDocumentSession session)
{
    session.Store(new SearchAudit(Guid.NewGuid(), request.Term));
    return new SearchResults(request.Term, request.Page, []);
}

// IQuerySession is Marten's read-only session and does NOT trigger transactional
// middleware — the right dependency for a QUERY endpoint that reads the database.
[WolverineQuery("/search/readonly")]
public static SearchResults SearchReadonly(SearchRequest request, IQuerySession session)
{
    return new SearchResults(request.Term, request.Page, []);
}
```

The practical guidance: for a `QUERY` endpoint that reads the database and should stay non-transactional, take Marten's read-only `IQuerySession` instead of an `IDocumentSession` — or, on EF Core, decorate the endpoint with `[NonTransactional]`.

---

## ⚠️ One caveat: OpenAPI 3.1

`QUERY` only became a first-class operation in **OpenAPI 3.2**. The OpenAPI 3.1 document produced by the Swashbuckle / `Microsoft.OpenApi` stack can't represent it, and naively handing it a `QUERY` operation throws and breaks document generation *for your whole application*.

So — matching ASP.NET Core's own behavior on OpenAPI 3.1 — Wolverine **gracefully omits `QUERY` endpoints from the generated OpenAPI document** rather than break generation for everything else. Your `QUERY` endpoints are fully routable and functional; they're simply not *described* in the OpenAPI 3.1 output. First-class OpenAPI docs can follow once the underlying stack emits 3.2.

---

## Testing QUERY endpoints (and an honest Alba caveat)

Wolverine's HTTP tests lean heavily on [Alba](https://jasperfx.github.io/alba/), and Alba's `Scenario()` helpers are wonderful — but they assume the *standard* verbs and can't express a `QUERY` request with a body. That's not a knock on Alba; `QUERY` is niche enough that the ergonomic helpers just haven't grown a case for it yet.

The good news is you don't need it to. You can stay inside the same Alba-based `IntegrationContext` host and drive a genuine `QUERY` straight through the test server's `HttpClient`:

```csharp
public class query_verb_support : IntegrationContext
{
    public query_verb_support(AppFixture fixture) : base(fixture) { }

    [Fact]
    public async Task query_endpoint_reads_request_body_and_returns_result()
    {
        // QUERY carries a request body (unlike GET). Alba's scenario helpers assume standard
        // verbs, so drive a genuine QUERY request through the test server's HttpClient.
        var client = Host.GetTestServer().CreateClient();

        var request = new HttpRequestMessage(new HttpMethod("QUERY"), "/search")
        {
            Content = JsonContent.Create(new SearchRequest("widget", 3))
        };

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var results = await response.Content.ReadFromJsonAsync<SearchResults>();
        results.ShouldNotBeNull();
        results.Term.ShouldBe("widget");
        results.Page.ShouldBe(3);
        results.Hits.ShouldBe(["widget-1", "widget-2", "widget-3"]);
    }
}
```

`Host.GetTestServer()` comes from the same Alba/`TestServer` plumbing your other tests already use — you're just hand-building the one request whose verb Alba can't spell for you.

You'll often also want to assert on routing and middleware wiring directly, without an HTTP round trip. Because the interesting behaviors here are about *metadata* and *middleware*, those checks read cleanly against the endpoint graph:

```csharp
[Fact]
public void query_route_is_registered_with_QUERY_method_metadata()
{
    var endpoint = EndpointFor("/search");
    var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
    methods.ShouldNotBeNull();
    methods.HttpMethods.ShouldContain("QUERY");
}

[Fact]
public void query_endpoint_is_not_wrapped_in_transactional_middleware()
{
    // Non-transactional because it takes no persistence dependency — NOT because QUERY is "safe".
    var chain = HttpChains.Chains.Single(x => x.RoutePattern!.RawText == "/search");
    chain.RequiresOutbox().ShouldBeFalse();
    chain.IsTransactional.ShouldBeFalse();
}

[Fact]
public void query_endpoint_with_document_session_is_transactional()
{
    // The dependency-based rule cuts both ways: an IDocumentSession dependency
    // attracts AutoApplyTransactions on a QUERY endpoint exactly as on a POST.
    var chain = HttpChains.Chains.Single(x => x.RoutePattern!.RawText == "/search/audited");
    chain.IsTransactional.ShouldBeTrue();
    chain.RequiresOutbox().ShouldBeFalse();
}
```

And, closing the loop on the OpenAPI caveat above, you can pin the "don't break the document" guarantee:

```csharp
[Fact]
public void swagger_generation_still_succeeds_with_a_query_endpoint()
{
    var generator = Host.Services.GetRequiredService<ISwaggerProvider>();
    var doc = generator.GetSwagger("default");

    // The QUERY endpoint is gracefully omitted, not thrown on.
    doc.Paths.ContainsKey("/search").ShouldBeFalse();
}
```

---

## The bottom line

`QUERY` support in Wolverine.HTTP is deliberately small: one attribute, no new configuration surface, and it reuses the same body binding and the same dependency-based middleware rules you already rely on for every other verb. If you've been `POST`-ing your searches and feeling slightly dirty about it, `[WolverineQuery]` is the honest verb you've been wanting.

Full documentation lives in the [HTTP Endpoints guide → The HTTP QUERY Method](https://wolverinefx.net/guide/http/endpoints.html#the-http-query-method).
