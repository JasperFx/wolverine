namespace Wolverine.EntityFrameworkCore;

public static class WolverineOptionsEntityFrameworkCoreConfigurationExtensions
{
    /// <summary>
    ///     Uses Entity Framework Core for Saga persistence and transactional
    ///     middleware
    /// </summary>
    /// <param name="options"></param>
    public static void UseEntityFrameworkCoreTransactions(this WolverineOptions options)
    {
        options.Include<EntityFrameworkCoreBackedPersistence>();
    }
}