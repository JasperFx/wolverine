using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Http;

namespace WolverineWebApi;

public class SpecialAttribute : ModifyEndpointAttribute
{
    public override void Modify(EndpointChain chain, GenerationRules rules)
    {
        chain.Middleware.Add(new CommentFrame("Just saying hello in the code! Also testing the usage of attributes to customize endpoints"));
    }
}