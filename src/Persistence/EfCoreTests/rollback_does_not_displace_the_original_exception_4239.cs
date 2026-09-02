using System.Reflection;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Xunit;

namespace EfCoreTests;

/// <summary>
/// GH-4239: the generated rollback threw over the exception that caused it, so the original failure
/// never reached a log or a dead letter queue.
///
/// <para>Two distinct defects lived in one line. With a token already cancelled on the way in --
/// which is what Wolverine's own DefaultExecutionTimeout hands to a nested InvokeAsync --
/// BeginTransactionAsync threw without creating a transaction, and the catch block's rollback then
/// threw "The connection does not have any active transactions", escaping the catch so `throw;` was
/// never reached. With a token cancelled after the transaction opened, the rollback was handed that
/// same cancelled token, threw TaskCanceledException, and left the transaction open until the
/// DbContext was disposed.</para>
/// </summary>
public class rollback_does_not_displace_the_original_exception_4239
{
    public class the_rollback_helper : IAsyncLifetime
    {
        private Bug4239DbContext _dbContext = null!;

        public async ValueTask InitializeAsync()
        {
            var builder = new DbContextOptionsBuilder<Bug4239DbContext>();
            builder.UseNpgsql(Servers.PostgresConnectionString);
            _dbContext = new Bug4239DbContext(builder.Options);

            await _dbContext.Database.OpenConnectionAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.Database.CloseConnectionAsync();
            await _dbContext.DisposeAsync();
        }

        [Fact]
        public async Task is_a_no_op_when_the_transaction_was_never_created()
        {
            // The GH-4239 case: BeginTransactionAsync threw on an already-cancelled token, so there is
            // nothing to roll back. The old generated code threw InvalidOperationException here, over
            // the top of whatever the handler had actually failed with.
            _dbContext.Database.CurrentTransaction.ShouldBeNull();

            await Should.NotThrowAsync(() => EfCoreTransactionRollback.SafeRollbackAsync(_dbContext));
        }

        [Fact]
        public async Task rolls_back_even_though_the_ambient_token_is_already_cancelled()
        {
            // The second GH-4239 case. The old code passed the failing token straight into the
            // rollback, so a cancellation cancelled the cleanup it had just caused and the transaction
            // stayed open until the DbContext was disposed.
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await _dbContext.Database.BeginTransactionAsync(CancellationToken.None);
            _dbContext.Database.CurrentTransaction.ShouldNotBeNull();

            await Should.NotThrowAsync(() => EfCoreTransactionRollback.SafeRollbackAsync(_dbContext));

            _dbContext.Database.CurrentTransaction.ShouldBeNull();
        }

        [Fact]
        public async Task rolls_back_a_healthy_transaction()
        {
            await _dbContext.Database.BeginTransactionAsync(CancellationToken.None);

            await EfCoreTransactionRollback.SafeRollbackAsync(_dbContext);

            _dbContext.Database.CurrentTransaction.ShouldBeNull();
        }

        // The two tests below characterize the calls the generated catch block USED to make. They are
        // the reason the helper exists, and they fail loudly if EF Core ever softens either behaviour
        // and makes the guard unnecessary.

        [Fact]
        public async Task characterize_the_old_rollback_with_no_transaction_open()
        {
            _dbContext.Database.CurrentTransaction.ShouldBeNull();

            // This threw out of the catch block, so the `throw;` on the next line never ran and the
            // handler's real exception was lost.
            await Should.ThrowAsync<InvalidOperationException>(
                () => _dbContext.Database.RollbackTransactionAsync(CancellationToken.None));
        }

        [Fact]
        public async Task characterize_the_old_rollback_with_a_cancelled_token()
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await _dbContext.Database.BeginTransactionAsync(CancellationToken.None);

            await Should.ThrowAsync<OperationCanceledException>(
                () => _dbContext.Database.RollbackTransactionAsync(cancelled.Token));

            // ...and the damage: the transaction the rollback was supposed to clean up is still open.
            _dbContext.Database.CurrentTransaction.ShouldNotBeNull();

            await EfCoreTransactionRollback.SafeRollbackAsync(_dbContext);
            _dbContext.Database.CurrentTransaction.ShouldBeNull();
        }
    }

    public class the_generated_catch_block
    {
        private static string generate()
        {
            var frame = new StartDatabaseTransactionForDbContext(typeof(Bug4239DbContext), IdempotencyStyle.None);

            var variables = new StubMethodVariables();
            variables.Store(new Variable(typeof(MessageContext), "context"));
            variables.Store(new Variable(typeof(Bug4239DbContext), "dbContext"));
            variables.Store(new Variable(typeof(CancellationToken), "cancellation"));

            frame.FindVariables(variables).ToList();

            using var writer = new SourceWriter();
            frame.GenerateCode(null!, writer);
            return writer.Code();
        }

        [Fact]
        public void rolls_back_through_the_guarded_helper()
        {
            generate().ShouldContain(nameof(EfCoreTransactionRollback.SafeRollbackAsync));
        }

        [Fact]
        public void never_hands_the_failing_cancellation_token_to_the_rollback()
        {
            // The token is still legitimately used by BeginTransactionAsync above; what must not
            // happen is a rollback that the very cancellation it is cleaning up can cancel.
            var code = generate();

            code.ShouldNotContain("RollbackTransactionAsync(cancellation)");
            code.ShouldContain("BeginTransactionAsync(cancellation)");
        }

        private sealed class StubMethodVariables : IMethodVariables
        {
            private readonly Dictionary<Type, Variable> _byType = new();

            public void Store(Variable variable) => _byType[variable.VariableType] = variable;

            public Variable FindVariable(Type type) => _byType[type];

            public Variable FindVariable(ParameterInfo parameter) => FindVariable(parameter.ParameterType);

            public Variable FindVariableByName(Type dependency, string name)
            {
                if (TryFindVariableByName(dependency, name, out var v)) return v;
                throw new ArgumentOutOfRangeException(nameof(name));
            }

            public bool TryFindVariableByName(Type dependency, string name, out Variable variable)
            {
                variable = default!;
                if (_byType.TryGetValue(dependency, out var found) && found.Usage == name)
                {
                    variable = found;
                    return true;
                }

                return false;
            }

            public Variable? TryFindVariable(Type type, VariableSource source)
                => _byType.TryGetValue(type, out var found) ? found : null;
        }
    }
}

public class Bug4239DbContext(DbContextOptions<Bug4239DbContext> options) : DbContext(options);
