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
            var call = MethodCall.For<IResult>(x => x.ExecuteAsync(null!));
            call.Target = chain.Method.Creates.First();
            chain.Postprocessors.Add(call);

            return true;
        }

        return false;
    }
}