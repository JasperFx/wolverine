using Wolverine.Http;

namespace WolverineWebApi;

public class WildcardEndpoint
{
  [WolverineGet("/wildcard/{*name}")]
  public string Wildcard(string name)
  {
    return name;
  }
}