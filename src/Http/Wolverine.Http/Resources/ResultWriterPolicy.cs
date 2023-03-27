using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Resources;

internal class ResultWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.Method.ReturnType.CanBeCastTo<IResult>())
        {
            var call = MethodCall.For<IResult>(x => x.ExecuteAsync(null));
            call.Target = chain.Method.ReturnVariable;
            chain.Postprocessors.Add(call);

            return true;
        }

        return false;
    }
}