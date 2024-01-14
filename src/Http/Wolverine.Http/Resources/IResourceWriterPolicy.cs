namespace Wolverine.Http.Resources;

#region sample_IResourceWriterPolicy
/// <summary>
///    Use to apply custom handling to the primary result of an HTTP endpoint handler
/// </summary>
public interface IResourceWriterPolicy
{
    /// <summary>
    ///  Called during bootstrapping to see whether this policy can handle the chain. If yes no further policies are tried.
    /// </summary>
    /// <param name="chain"> The chain to test against</param>
    /// <returns>True if it applies to the chain, false otherwise</returns>
    bool TryApply(HttpChain chain);
}

#endregion