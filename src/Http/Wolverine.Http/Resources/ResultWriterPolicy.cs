using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Resources;

internal class ResultWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.ResourceType.CanBeCastTo<IResult>())
        {
        // GH-4789: ResourceVariable first, Method.Creates.First() only as the fallback. These policies
        // gate on chain.ResourceType, and IChain.UseForResponse CHANGES that -- it is how an IResponseAware
        // such as Marten's UpdatedAggregate swaps the declared response for another call's return value. So
        // reading the handler method's own first created variable instead wrote the WRONG one whenever a
        // response had been rewritten, generating code that did not compile. JsonResourceWriterPolicy and
        // ContentNegotiationPolicy already did this correctly, which is why the JSON path (and therefore
        // UpdatedAggregate) worked while the string, IResult and status-code paths did not.
            var call = MethodCall.For<IResult>(x => x.ExecuteAsync(null!));
            call.Target = chain.ResourceVariable ?? chain.Method.Creates.First();
            chain.Postprocessors.Add(call);

            return true;
        }

        return false;
    }
}