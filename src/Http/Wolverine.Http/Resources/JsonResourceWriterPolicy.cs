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
                if (NewtonsoftCodeGen is null)
                {
                    throw new InvalidOperationException(
                        $"{nameof(JsonUsage)}.{nameof(JsonUsage.NewtonsoftJson)} is selected for HTTP JSON serialization, " +
                        "but no Newtonsoft codegen hook is registered. Install the WolverineFx.Http.Newtonsoft NuGet package " +
                        "and call opts.UseNewtonsoftJsonForSerialization() inside MapWolverineEndpoints. " +
                        "See https://wolverinefx.net/guide/http/json.html#using-newtonsoft-json.");
                }

                chain.Postprocessors.Add(NewtonsoftCodeGen.CreateWriteJsonFrame(resourceVariable));
            }

            return true;
        }

        return false;
    }

    public JsonUsage Usage { get; set; } = JsonUsage.SystemTextJson;

    /// <summary>
    ///     Set by <see cref="HttpGraph.UseNewtonsoftJson"/> when the WolverineFx.Http.Newtonsoft
    ///     companion package is wired up. Required when <see cref="Usage"/> is
    ///     <see cref="JsonUsage.NewtonsoftJson"/>.
    /// </summary>
    internal INewtonsoftHttpCodeGen? NewtonsoftCodeGen { get; set; }
}
