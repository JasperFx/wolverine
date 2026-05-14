using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

// ReSharper disable once InconsistentNaming
//
// AOT note (#2746): This whole class is the EF Core codegen frame provider.
// Every site here closes generic helper types (ApplyAncillaryStoreFrame<>,
// CreateTenantedDbContext<>, EfCoreStorageActionApplier.ApplyAction<,>,
// IDbContextBuilder<>) over runtime saga / entity / DbContext types at
// codegen time, or reflectively walks DbContext type members to discover
// SaveChangesAsync / Version / etc. AOT-clean apps run pre-generated
// frames in TypeLoadMode.Static and bypass this class entirely; Dynamic-
// mode codegen consumers preserve their saga / DbContext types via
// TrimmerRootDescriptor. Same chunk P (saga frame providers) pattern.
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "EFCore codegen frame provider — Dynamic-mode codegen path; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2067",
    Justification = "EFCore codegen frame provider — entity / DbContext types statically rooted via persistence registration. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2075",
    Justification = "EFCore codegen frame provider — DbContext.SaveChangesAsync etc. lookups on statically-rooted DbContext types. See AOT guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "EFCore codegen frame provider — closed generics over runtime DbContext / entity types at codegen time. See AOT guide.")]
internal class EFCorePersistenceFrameProvider : IPersistenceFrameProvider
{
    public const string UsingEfCoreTransaction = "uses_efcore_transaction";
    public const string TransactionModeKey = "TransactionMiddlewareMode";
    private ImHashMap<Type, Type?> _dbContextTypes = ImHashMap<Type, Type?>.Empty;

    public TransactionMiddlewareMode DefaultMode { get; set; } = TransactionMiddlewareMode.Eager;

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        var dbContextType = TryDetermineDbContextType(entityType, container);
        persistenceService = dbContextType!;
        return dbContextType != null;
    }
    
    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Variable.VariableType returns the spec type without DAM annotation. FindInterfaceThatCloses inspects the generic-interface graph for IQueryPlan<,> / IBatchQueryPlan<,>; user spec types are statically rooted by handler discovery and preserved by their registration.")]
    public bool TryBuildFetchSpecificationFrame(
        Variable specVariable,
        IServiceContainer container,
        [NotNullWhen(true)] out Frame? frame,
        [NotNullWhen(true)] out Variable? result)
    {
        if (specVariable is null)
        {
            frame = null;
            result = null;
            return false;
        }

        var specType = specVariable.VariableType;

        // Wolverine.EntityFrameworkCore spec shapes: IQueryPlan<TDbContext, TResult>
        // or IBatchQueryPlan<TDbContext, TResult>, both in Wolverine.EntityFrameworkCore namespace.
        var batchPlan = specType.FindInterfaceThatCloses(typeof(IBatchQueryPlan<,>));
        var queryPlan = specType.FindInterfaceThatCloses(typeof(IQueryPlan<,>));

        var isEfBatchPlan = batchPlan is not null
                            && batchPlan.Namespace == typeof(IBatchQueryPlan<,>).Namespace;
        var isEfPlan = queryPlan is not null
                       && queryPlan.Namespace == typeof(IQueryPlan<,>).Namespace;

        if (!isEfBatchPlan && !isEfPlan)
        {
            frame = null;
            result = null;
            return false;
        }

        var fetch = new FetchSpecificationFrame(specVariable);
        frame = fetch;
        result = fetch.Result;
        return true;
    }

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
        return DetermineDeleteFrame(null!, variable, container);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(entityType, container);
        
        var method = typeof(EfCoreStorageActionApplier).GetMethod("ApplyAction")!
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

        var mode = ResolveEffectiveMode(chain);

        var runtime = container.Services.GetRequiredService<IWolverineRuntime>();
        if (runtime.Stores.HasAncillaryStoreFor(dbContextType))
        {
            var frame = typeof(ApplyAncillaryStoreFrame<>).CloseAndBuildAs<Frame>(dbContextType);
            chain.Middleware.Insert(0, frame);
        }

        if (mode == TransactionMiddlewareMode.Eager)
        {
            if (isMultiTenanted(container, dbContextType))
            {
                var createContext = typeof(CreateTenantedDbContext<>).CloseAndBuildAs<Frame>(dbContextType);

                chain.Middleware.Insert(0, createContext);
                chain.Middleware.Insert(0, new StartDatabaseTransactionForDbContext(dbContextType, chain.Idempotency));
            }
            else
            {
                chain.Middleware.Insert(0, new EnrollDbContextInTransaction(dbContextType, chain.Idempotency));
            }
        }

        var saveChangesAsync =
            dbContextType.GetMethod(nameof(DbContext.SaveChangesAsync), [typeof(CancellationToken)]);

        var call = new MethodCall(dbContextType, saveChangesAsync!)
        {
            CommentText = "Added by EF Core Transaction Middleware"
        };

        chain.Postprocessors.Add(call);

        // Eager mode wraps the rest of the chain in EnrollDbContextInTransaction's
        // try/catch and ends the try block with `efCoreEnvelopeTransaction.CommitAsync(...)`.
        // EfCoreEnvelopeTransaction.CommitAsync commits the EF Core transaction and THEN
        // flushes outgoing messages - that's the only ordering that lets the post-send
        // outbox bookkeeping see the wolverine_outgoing row this chain just inserted.
        // Adding a standalone FlushOutgoingMessages postprocessor here would inject the
        // flush BEFORE the commit, and the post-send DELETE would no-op against the
        // still-uncommitted INSERT, leaving the row stranded for the durability agent
        // (at-least-once instead of exactly-once). See the dmytro-pryvedeniuk/outbox
        // sample report and the failing HTTP test in
        // Wolverine.Http.Tests/Bug_efcore_outbox_flush_before_commit.cs.
        //
        // Lightweight mode skips EnrollDbContextInTransaction (no try-block wrap, no
        // CommitAsync), so the standalone FlushOutgoingMessages postprocessor is the
        // only flush trigger and must stay.
        if (mode != TransactionMiddlewareMode.Eager
            && chain.RequiresOutbox() && chain.ShouldFlushOutgoingMessages())
        {
#pragma warning disable CS4014
            chain.Postprocessors.Add(new FlushOutgoingMessages());
#pragma warning restore CS4014
        }
    }

    /// <summary>
    /// Resolves the effective transaction mode for a chain by checking (in order):
    /// 1. The chain tag (set when TransactionalAttribute.Modify has already run)
    /// 2. The [Transactional] attribute directly on handler methods/types (for when
    ///    side effects are processed by SideEffectPolicy before the attribute's Modify runs)
    /// 3. The configured DefaultMode
    /// </summary>
    internal TransactionMiddlewareMode ResolveEffectiveMode(IChain chain)
    {
        // Check the tag first (set by TransactionalAttribute.Modify when it has already run)
        if (chain.Tags.TryGetValue(TransactionModeKey, out var modeObj))
        {
            return (TransactionMiddlewareMode)modeObj;
        }

        // Check handler method and type attributes directly for when SideEffectPolicy
        // processes Storage return types before TransactionalAttribute.Modify has run
        foreach (var call in chain.HandlerCalls())
        {
            var methodAttr = call.Method.GetCustomAttribute<TransactionalAttribute>();
            if (methodAttr is { IsModeExplicitlySet: true })
            {
                // Cache it in the tag for subsequent calls
                chain.Tags[TransactionModeKey] = methodAttr.Mode;
                return methodAttr.Mode;
            }

            var typeAttr = call.HandlerType.GetCustomAttribute<TransactionalAttribute>();
            if (typeAttr is { IsModeExplicitlySet: true })
            {
                chain.Tags[TransactionModeKey] = typeAttr.Mode;
                return typeAttr.Mode;
            }
        }

        return DefaultMode;
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

        var mode = ResolveEffectiveMode(chain);

        if (mode == TransactionMiddlewareMode.Eager)
        {
            if (isMultiTenanted(container, dbType))
            {
                var createContext = typeof(CreateTenantedDbContext<>).CloseAndBuildAs<Frame>(dbType);
                chain.Middleware.Insert(0, createContext);
                chain.Middleware.Insert(0, new StartDatabaseTransactionForDbContext(dbType, chain.Idempotency));
            }
            else
            {
                chain.Middleware.Insert(0, new EnrollDbContextInTransaction(dbType, chain.Idempotency));
            }
        }

        var saveChangesAsync =
            dbType.GetMethod(nameof(DbContext.SaveChangesAsync), [typeof(CancellationToken)]);

        var call = new MethodCall(dbType, saveChangesAsync!)
        {
            CommentText = "Added by EF Core Transaction Middleware"
        };

        chain.Postprocessors.Add(call);

        // See the rationale in the no-entity ApplyTransactionSupport overload above.
        // Same constraint: in Eager mode, EnrollDbContextInTransaction's CommitAsync is
        // the sole legitimate flush trigger; a standalone postprocessor would flush
        // before the EF Core commit and strand the wolverine_outgoing row.
        if (mode != TransactionMiddlewareMode.Eager
            && chain.RequiresOutbox() && chain.ShouldFlushOutgoingMessages())
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
            var dbContext = (DbContext)nested.ServiceProvider.GetService(candidate)!;
            try
            {
                if (dbContext!.Model.FindEntityType(entityType) != null)
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