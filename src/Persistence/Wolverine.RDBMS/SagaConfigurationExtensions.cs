using Microsoft.Extensions.DependencyInjection;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.RDBMS;

public static class SagaConfigurationExtensions
{
    /// <summary>
    /// Add storage for a persistent saga with the Wolverine lightweight saga storage
    /// model. This can be omitted, but is necessary for database schema generation and migration
    /// support
    /// </summary>
    /// <param name="options"></param>
    /// <param name="tableName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static WolverineOptions AddSagaType<T>(this WolverineOptions options, string? tableName = null)
    {
        var storage = new SagaTableDefinition(typeof(T), tableName);
        options.Services.AddSingleton(storage);
        return options;
    }
    
    /// <summary>
    /// Add storage for a persistent saga with the Wolverine lightweight saga storage
    /// model. This can be omitted, but is necessary for database schema generation and migration
    /// support
    /// </summary>
    /// <param name="options"></param>
    /// <param name="tableName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static WolverineOptions AddSagaType(this WolverineOptions options, Type sagaType, string? tableName = null)
    {
        var storage = new SagaTableDefinition(sagaType, tableName);
        options.Services.AddSingleton(storage);
        return options;
    }
}