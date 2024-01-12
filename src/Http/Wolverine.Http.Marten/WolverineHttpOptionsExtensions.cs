namespace Wolverine.Http.Marten;

public static class WolverineHttpOptionsExtensions
{
    /// <summary>
    /// Adds an <see cref="IResourceWriterPolicy"/> that streams <see cref="ICompiledQuery"/> 
    /// </summary>
    /// <param name="options">Options to apply policy on</param>
    public static void UseMartenCompiledQueryResultPolicy(this WolverineHttpOptions options)
    {
            options.AddResourceWriterPolicy<CompiledQueryWriterPolicy>();
    } 
}