using ImTools;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

// ReSharper disable once InconsistentNaming
internal class EFCorePersistenceFrameProvider : IPersistenceFrameProvider
{
    public const string UsingEfCoreTransaction = "uses_efcore_transaction";
    private ImHashMap<Type, Type?> _dbContextTypes = ImHashMap<Type, Type?>.Empty;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        var dbContextType = TryDetermineDbContextType(entityType, container);
        persistenceService = dbContextType!;
        return dbContextType != null;
    }
    
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(sagaType, container);
        using var nested = container.Services.CreateScope();
        var context = (DbContext)nested.ServiceProvider.GetRequiredService(dbContextType);
        var config = context.Model.FindEntityType(sagaType);
        if (config == null)
        {
            throw new InvalidOperationException(
                $"Could not find entity configuration for {sagaType.FullNameInCode()} in DbContext {context}");
        }

        return config.FindPrimaryKey()?.GetKeyType() ??
               throw new InvalidOperationException(
                   $"No known primary key for {sagaType.FullNameInCode()} in DbContext {context}");
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        var dbContextType = DetermineDbContextType(sagaType, container);
        return new LoadEntityFrame(dbContextType, sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(saga.VariableType, container);
        return new DbContextOperationFrame(dbContextType, saga, nameof(DbContext.Add));
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(saga.VariableType, container);
        var method =
            dbContextType.GetMethod(nameof(DbContext.SaveChangesAsync), [typeof(CancellationToken)]);

        var call = new MethodCall(dbContextType, method!);
        call.CommentText = "Committing any pending entity changes to the database";
        call.ReturnVariable!.OverrideName(call.ReturnVariable.Usage + "1");

        return new WrapSagaConcurrencyException(saga, call);
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        var version = saga.VariableType.GetProperty("Version");
        if (version == null || !(version.CanRead && version.CanWrite))
        {
            return new CommentFrame("No explicit update necessary with EF Core without a Version property");
        }
        
        var dbContextType = DetermineDbContextType(saga.VariableType, container);
        return new IncrementSagaVersionIfNecessary(dbContextType, saga);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(saga.VariableType, container);
        return new DbContextOperationFrame(dbContextType, saga, nameof(DbContext.Remove));
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return DetermineDeleteFrame(null, variable, container);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(entityType, container);
        
        var method = typeof(EfCoreStorageActionApplier).GetMethod("ApplyAction")
            .MakeGenericMethod(entityType, dbContextType);

        var call = new MethodCall(typeof(EfCoreStorageActionApplier), method);
        call.Arguments[1] = action;

        return call;
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return DetermineUpdateFrame(saga, container);
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (chain.Tags.ContainsKey(UsingEfCoreTransaction)) return;
        chain.Tags.Add(UsingEfCoreTransaction, true);

        var dbContextType = DetermineDbContextType(chain, container);
        var runtime = container.Services.GetRequiredService<IWolverineRuntime>();
        if (runtime.Stores.HasAncillaryStoreFor(dbContextType))
        {
            var frame = typeof(ApplyAncillaryStoreFrame<>).CloseAndBuildAs<Frame>(dbContextType);
            chain.Middleware.Insert(0, frame);
        }
        
        if (isMultiTenanted(container, dbContextType))
        {
            var createContext = typeof(CreateTenantedDbContext<>).CloseAndBuildAs<Frame>(dbContextType);

            chain.Middleware.Insert(0, createContext);
            chain.Middleware.Insert(0, new EnrollTenantedDbContextInTransaction(dbContextType, chain.Idempotency));
        }
        else
        {
            chain.Middleware.Insert(0, new EnrollDbContextInTransaction(dbContextType, chain.Idempotency));
        }

        var saveChangesAsync =
            dbContextType.GetMethod(nameof(DbContext.SaveChangesAsync), [typeof(CancellationToken)]);

        var call = new MethodCall(dbContextType, saveChangesAsync!)
        {
            CommentText = "Added by EF Core Transaction Middleware"
        };

        chain.Postprocessors.Add(call);

        if (chain.RequiresOutbox() && chain.ShouldFlushOutgoingMessages())
        {
#pragma warning disable CS4014
            chain.Postprocessors.Add(new FlushOutgoingMessages());
#pragma warning restore CS4014
        }
    }

    private bool isMultiTenanted(IServiceContainer container, Type dbContextType)
    {
        return container.HasRegistrationFor(typeof(IDbContextBuilder<>).MakeGenericType(dbContextType));
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        if (chain.Tags.ContainsKey(UsingEfCoreTransaction)) return;
        chain.Tags.Add(UsingEfCoreTransaction, true);

        var dbType = DetermineDbContextType(entityType, container);
        if (isMultiTenanted(container, dbType))
        {
            var createContext = typeof(CreateTenantedDbContext<>).CloseAndBuildAs<Frame>(dbType);
            chain.Middleware.Insert(0, createContext);
            chain.Middleware.Insert(0, new EnrollTenantedDbContextInTransaction(dbType, chain.Idempotency));
        }
        else
        {
            chain.Middleware.Insert(0, new EnrollDbContextInTransaction(dbType, chain.Idempotency));
        }
        
        var saveChangesAsync =
            dbType.GetMethod(nameof(DbContext.SaveChangesAsync), [typeof(CancellationToken)]);

        var call = new MethodCall(dbType, saveChangesAsync!)
        {
            CommentText = "Added by EF Core Transaction Middleware"
        };

        chain.Postprocessors.Add(call);

        if (chain.RequiresOutbox() && chain.ShouldFlushOutgoingMessages())
        {
#pragma warning disable CS4014
            chain.Postprocessors.Add(new FlushOutgoingMessages());
#pragma warning restore CS4014
        }
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain saga)
        {
            var sagaType = saga.SagaType;
            return TryDetermineDbContextType(sagaType, container) != null;
        }

        var serviceDependencies = chain.ServiceDependencies(container, Type.EmptyTypes).ToArray();
        return serviceDependencies.Any(x => x.CanBeCastTo<DbContext>());
    }

    internal Type? TryDetermineDbContextType(Type entityType, IServiceContainer container)
    {
        if (_dbContextTypes.TryFind(entityType, out var dbContextType))
        {
            return dbContextType;
        }

        using var nested = container.Services.CreateScope();

        // Need to check for multi-tenanted candidates FIRST
        var multiTenantCandidates = container.FindMatchingServices(type => type.Closes(typeof(IDbContextBuilder<>)))
            .Select(x => x.ServiceType).ToArray();

        foreach (var candidate in multiTenantCandidates)
        {
            var builder = (IDbContextBuilder)nested.ServiceProvider.GetRequiredService(candidate);
            var dbContext = builder.BuildForMain();
            
            try
            {
                if (dbContext.Model.FindEntityType(entityType) != null)
                {
                    _dbContextTypes = _dbContextTypes.AddOrUpdate(entityType, builder.DbContextType);
                    return candidate;
                }
            }
            catch (InvalidOperationException e)
            {
                var logger = container.Services.GetService<ILogger<EFCorePersistenceFrameProvider>>();
                logger?.LogError(e, "Error trying to use DbContext type {DbContextType}", candidate.FullNameInCode());
            }
        }
        
        var candidates = container.FindMatchingServices(type => type.CanBeCastTo<DbContext>())
            .Select(x => x.ServiceType).ToArray();

        foreach (var candidate in candidates)
        {
            var dbContext = (DbContext)nested.ServiceProvider.GetService(candidate);
            try
            {
                if (dbContext.Model.FindEntityType(entityType) != null)
                {
                    _dbContextTypes = _dbContextTypes.AddOrUpdate(entityType, candidate);
                    return candidate;
                }
            }
            catch (InvalidOperationException e)
            {
                var logger = container.Services.GetService<ILogger<EFCorePersistenceFrameProvider>>();
                logger?.LogError(e, "Error trying to use DbContext type {DbContextType}", candidate.FullNameInCode());
            }
        }

        _dbContextTypes = _dbContextTypes.AddOrUpdate(entityType, null);
        return null;
    }
    
    internal Type DetermineDbContextType(Type entityType, IServiceContainer container)
    {
        var contextType = TryDetermineDbContextType(entityType, container);
        if (contextType == null)
        {
            throw new ArgumentOutOfRangeException("Unable to determine a DbContext type that persists " +
                                                  entityType.FullNameInCode());
        }

        return contextType;
    }

    public Type DetermineDbContextType(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain saga)
        {
            return DetermineDbContextType(saga.SagaType, container);
        }
// START HERE. Look for any IStorageAction<T>, and use the T
        var contextTypes = chain.ServiceDependencies(container, Type.EmptyTypes).Where(x => x.CanBeCastTo<DbContext>()).ToArray();

        if (contextTypes.Length == 0)
        {
            var sagaType = chain.HandlerCalls().SelectMany(x => x.Creates)
                .Where(x => x.VariableType.CanBeCastTo<Saga>())
                .Select(x => x.VariableType)
                .FirstOrDefault();

            if (sagaType != null)
            {
                return DetermineDbContextType(sagaType, container);
            }
            
            throw new InvalidOperationException(
                $"Cannot determine the {nameof(DbContext)} type for {chain.Description}");
        }

        if (contextTypes.Length > 1)
        {
            throw new InvalidOperationException(
                $"Cannot determine the {nameof(DbContext)} type for {chain.Description}, multiple {nameof(DbContext)} types detected: {contextTypes.Select(x => x.Name).Join(", ")}");
        }

        return contextTypes.Single();
    }

    public class IncrementSagaVersionIfNecessary : SyncFrame
    {
        private readonly Type _dbContextType;
        private readonly Variable _saga;
        private Variable? _context;

        public IncrementSagaVersionIfNecessary(Type dbContextType, Variable saga)
        {
            _dbContextType = dbContextType;
            _saga = saga;
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            yield return _saga;

            _context = chain.FindVariable(_dbContextType);
            yield return _context;
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteLine("");
            writer.WriteComment("If the saga state changed, then increment its version to support optimistic concurrency");
            writer.WriteLine($"if ({_context!.Usage}.Entry({_saga.Usage}).State == {typeof(EntityState).FullName}.Modified) {{ {_saga.Usage}.Version += 1; }}");

            Next?.GenerateCode(method, writer);
        }
    }

    public class WrapSagaConcurrencyException : SyncFrame
    {
        private readonly Variable _saga;
        private readonly Frame _frame;

        public WrapSagaConcurrencyException(Variable saga, Frame frame)
        {
            _saga = saga;
            _frame = frame;
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            foreach (var variable in _frame.FindVariables(chain)) yield return variable;
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.Write("BLOCK:try");
            _frame.GenerateCode(method, writer);
            writer.FinishBlock();

            writer.Write($"BLOCK:catch ({typeof(DbUpdateConcurrencyException).FullNameInCode()} error)");
            writer.WriteComment("Only intercepts concurrency error on the saga itself");

            writer.Write($"BLOCK:if ({typeof(Enumerable).FullNameInCode()}.Any(error.Entries, e => e.Entity == {_saga.Usage}))");
            writer.WriteLine($"throw new {typeof(SagaConcurrencyException).FullNameInCode()}($\"Saga of type {_saga.VariableType.FullNameInCode()} and identity {SagaChain.SagaIdVariableName} cannot be updated because of optimistic concurrency violations\");");
            writer.FinishBlock();

            writer.WriteComment("Rethrow any other exception");
            writer.WriteLine("throw;");
            writer.FinishBlock();

            Next?.GenerateCode(method, writer);
        }

    }
}