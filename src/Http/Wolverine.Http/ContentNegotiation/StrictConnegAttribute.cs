using JasperFx.CodeGeneration;

namespace Wolverine.Http.ContentNegotiation;

/// <summary>
/// Apply strict content negotiation to this endpoint. When the client's Accept header
/// does not match any registered content type writer, returns HTTP 406 Not Acceptable
/// instead of falling back to JSON.
/// </summary>
public class StrictConnegAttribute : ModifyHttpChainAttribute
{
    public override void Modify(HttpChain chain, GenerationRules rules)
    {
        chain.ConnegMode = ConnegMode.Strict;
    }
}
