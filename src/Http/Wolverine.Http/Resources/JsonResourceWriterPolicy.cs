using JasperFx.CodeGeneration.Frames;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Resources;

internal class JsonResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.HasResourceType())
        {
            var resourceVariable = chain.ResourceVariable ?? chain.Method.Creates.First();
            resourceVariable.OverrideName(resourceVariable.Usage + "_response");

            if (Usage == JsonUsage.SystemTextJson)
            {
                chain.Postprocessors.Add(new WriteJsonFrame(resourceVariable));
            }
            else
            {
                var frame = new MethodCall(typeof(NewtonsoftHttpSerialization),
                    nameof(NewtonsoftHttpSerialization.WriteJsonAsync));
                frame.Arguments[1] = resourceVariable;

                chain.Postprocessors.Add(frame);
            }

            return true;
        }

        return false;
    }

    public JsonUsage Usage { get; set; } = JsonUsage.SystemTextJson;
}