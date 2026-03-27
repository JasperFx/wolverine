using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.RDBMS;

public static class SagaConfigurationExtensions
{
    /// <summary>
    /// Add storage for a persistent saga with the Wolverine lightweight saga storage
    /// model. This can be omitted, but is necessary for database schema generation and migration
    /// support
    ///
    /// Do NOT use this if you mean for the saga to be persisted by Marten or EF Core
    /// </summary>
    /// <param name="options"></param>
    /// <param name="tableName"></param>
    /// <param name="useNVarCharForStringId">
    /// Opt in to a SQL Server schema change for string saga ids from the default inferred
    /// varchar(100) to nvarchar(100). This only applies to Wolverine's lightweight SQL Server saga storage.
    /// </param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static WolverineOptions AddSagaType<T>(this WolverineOptions options, string? tableName = null, bool useNVarCharForStringId = false) where T : Saga
    {
        var storage = new SagaTableDefinition(typeof(T), tableName, useNVarCharForStringId);
        options.Services.AddSingleton(storage);
        return options;
    }
    
    /// <summary>
    /// Add storage for a persistent saga with the Wolverine lightweight saga storage
    /// model. This can be omitted, but is necessary for database schema generation and migration
    /// support
    ///
    /// Do NOT use this if you mean for the saga to be persisted by Marten or EF Core
    /// </summary>
    /// <param name="options"></param>
    /// <param name="tableName"></param>
    /// <param name="useNVarCharForStringId">
    /// Opt in to a SQL Server schema change for string saga ids from the default inferred
    /// varchar(100) to nvarchar(100). This only applies to Wolverine's lightweight SQL Server saga storage.
    /// </param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static WolverineOptions AddSagaType(this WolverineOptions options, Type sagaType, string? tableName = null, bool useNVarCharForStringId = false)
    {
        if (!sagaType.CanBeCastTo<Saga>())
        {
            throw new ArgumentOutOfRangeException(nameof(sagaType),
                $"Type {sagaType.FullNameInCode()} does not inherit from {typeof(Saga).FullNameInCode()}");
        }
        
        var storage = new SagaTableDefinition(sagaType, tableName, useNVarCharForStringId);
        options.Services.AddSingleton(storage);
        return options;
    }
}