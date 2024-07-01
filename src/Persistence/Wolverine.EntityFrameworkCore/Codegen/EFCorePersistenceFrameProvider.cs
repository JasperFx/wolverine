using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
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

        return call;
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("No explicit update necessary with EF Core");
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        var dbContextType = DetermineDbContextType(saga.VariableType, container);
        return new DbContextOperationFrame(dbContextType, saga, nameof(DbContext.Remove));
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (chain.Tags.ContainsKey(UsingEfCoreTransaction)) return;
        chain.Tags.Add(UsingEfCoreTransaction, true);

        var dbType = DetermineDbContextType(chain, container);

        chain.Middleware.Insert(0, new EnrollDbContextInTransaction(dbType));

        var saveChangesAsync =
            dbType.GetMethod(nameof(DbContext.SaveChangesAsync), [typeof(CancellationToken)]);

        var call = new MethodCall(dbType, saveChangesAsync!)
        {
            CommentText = "Added by EF Core Transaction Middleware"
        };

        chain.Postprocessors.Add(call);

        chain.Postprocessors.Add(new CommitDbContextTransactionIfNecessary());

        if (chain.RequiresOutbox() && chain.ShouldFlushOutgoingMessages())
        {
#pragma warning disable CS4014
            chain.Postprocessors.Add(MethodCall.For<MessageContext>(x => x.FlushOutgoingMessagesAsync()));
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

        var contextTypes = chain.ServiceDependencies(container, Type.EmptyTypes).Where(x => x.CanBeCastTo<DbContext>()).ToArray();

        if (contextTypes.Length == 0)
        {
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

    public class EnrollDbContextInTransaction : SyncFrame
    {
        private readonly Type _dbContextType;
        private readonly Variable? _envelopeTransaction;
        private Variable? _context;
        private Variable? _dbContext;

        public EnrollDbContextInTransaction(Type dbContextType)
        {
            _dbContextType = dbContextType;
            _envelopeTransaction = Create(typeof(IEnvelopeTransaction));
        }

        public override IEnumerable<Variable> Creates
        {
            get { yield return _envelopeTransaction!; }
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            writer.WriteLine("");
            writer.WriteComment(
                "Enroll the DbContext & IMessagingContext in the outgoing Wolverine outbox transaction");
            writer.Write(
                $"var {_envelopeTransaction!.Usage} = Wolverine.EntityFrameworkCore.WolverineEntityCoreExtensions.BuildTransaction({_dbContext!.Usage}, {_context!.Usage});");
            writer.Write(
                $"await {_context.Usage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.Usage});");

            Next?.GenerateCode(method, writer);
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _context = chain.FindVariable(typeof(MessageContext));
            yield return _context;

            _dbContext = chain.FindVariable(_dbContextType);
            yield return _dbContext;
        }
    }

    public class CommitDbContextTransactionIfNecessary : SyncFrame
    {
        private Variable? _envelopeTransaction;

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            if (_envelopeTransaction != null)
            {
                writer.WriteComment(
                    "If we have separate context for outbox and application, then we need to manually commit the transaction");
                writer.Write(
                    $"if ({_envelopeTransaction.Usage} is Wolverine.EntityFrameworkCore.Internals.RawDatabaseEnvelopeTransaction rawTx) {{ await rawTx.CommitAsync(); }}");
            }

            Next?.GenerateCode(method, writer);
        }

        public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
        {
            _envelopeTransaction = chain.TryFindVariable(typeof(IEnvelopeTransaction), VariableSource.NotServices);
            if (_envelopeTransaction != null)
            {
                yield return _envelopeTransaction;
            }
        }
    }
}

internal class DbContextOperationFrame : SyncFrame
{
    private readonly Type _dbContextType;
    private readonly string _methodName;
    private readonly Variable _saga;
    private Variable? _context;

    public DbContextOperationFrame(Type dbContextType, Variable saga, string methodName)
    {
        _dbContextType = dbContextType;
        _saga = saga;
        _methodName = methodName;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Registering the Saga entity change");
        writer.Write($"{_context!.Usage}.{_methodName}({_saga.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

internal class LoadEntityFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _context;

    public LoadEntityFrame(Type dbContextType, Type sagaType, Variable sagaId)
    {
        _dbContextType = dbContextType;
        _sagaId = sagaId;

        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Trying to load the existing Saga data");
        writer.Write(
            $"var {Saga.Usage} = await {_context!.Usage}.{nameof(DbContext.FindAsync)}<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}