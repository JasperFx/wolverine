using System.ComponentModel.DataAnnotations;
using Marten;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Bugs;

public record SomeRequest(string Name);

public class SomeEndpoint
{
    // https://wolverine.netlify.app/guide/http/middleware.html#required-inputs
    public static Task<SomeDocument?> LoadAsync(string id,
        IDocumentSession session)
        => session.LoadAsync<SomeDocument>(id);

    [WolverinePost("/some/{id}")]
    // If the "id" parameter is removed here, LoadAsync above would receive
    // the HttpContext.TraceIdentifier as its "id" parameter value.
    public static StoreDoc<SomeDocument> Handle(/* string id, -- it works if I declare "id" here, although I do not need it */
        SomeRequest request,
        [Required] SomeDocument doc)
    {
        return MartenOps.Store(doc);
    }
}

public class SomeDocument
{
    public string Id { get; set; }
}