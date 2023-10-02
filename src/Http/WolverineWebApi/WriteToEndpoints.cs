using Marten;
using Marten.AspNetCore;
using Wolverine.Http;

namespace WolverineWebApi;

public static class WriteToEndpoints
{
  [WolverineGet("/write-to/{id}")]
  public static Task GetAssetCodeView(Guid id, IQuerySession session, HttpContext context)
    => session.Json.WriteById<Issue>(id, context);
}