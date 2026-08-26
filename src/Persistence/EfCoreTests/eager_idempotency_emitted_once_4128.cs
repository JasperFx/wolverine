using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Xunit;

namespace EfCoreTests;

// GH-4128: StartDatabaseTransactionForDbContext -- the multi-tenanted counterpart to
// EnrollDbContextInTransaction -- emitted the eager idempotency check twice under identical
// guards, once above and once below the BeginTransactionAsync block. On the paths where
// AssertEagerIdempotencyAsync falls through to Runtime.Storage.Inbox.ExistsAsync (which does not
// set Envelope.WasPersistedInInbox), that meant two identical inbox existence queries per message.
public class eager_idempotency_emitted_once_4128
{
    [Theory]
    [InlineData(IdempotencyStyle.Eager)]
    [InlineData(IdempotencyStyle.Optimistic)]
    public void emits_the_eager_check_exactly_once_and_after_the_transaction_starts(IdempotencyStyle style)
    {
        var code = generate(style);

        countOf(code, nameof(MessageContext.AssertEagerIdempotencyAsync)).ShouldBe(1);

        // ...and it lands after BeginTransactionAsync, matching EnrollDbContextInTransaction
        code.IndexOf(nameof(MessageContext.AssertEagerIdempotencyAsync), StringComparison.Ordinal)
            .ShouldBeGreaterThan(code.IndexOf("BeginTransactionAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void emits_no_check_at_all_for_none()
    {
        generate(IdempotencyStyle.None)
            .ShouldNotContain(nameof(MessageContext.AssertEagerIdempotencyAsync));
    }

    private static string generate(IdempotencyStyle style)
    {
        var frame = new StartDatabaseTransactionForDbContext(typeof(LookupDbContext), style);

        var variables = new StubMethodVariables();
        variables.Store(new Variable(typeof(MessageContext), "context"));
        variables.Store(new Variable(typeof(LookupDbContext), "dbContext"));
        variables.Store(new Variable(typeof(CancellationToken), "cancellation"));

        frame.FindVariables(variables).ToList();

        using var writer = new SourceWriter();
        frame.GenerateCode(null!, writer);
        return writer.Code();
    }

    private static int countOf(string code, string token)
    {
        var count = 0;
        var index = code.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = code.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private sealed class StubMethodVariables : IMethodVariables
    {
        private readonly Dictionary<Type, Variable> _byType = new();

        public Variable FindVariable(Type type) => _byType[type];

        public Variable FindVariable(ParameterInfo parameter) => FindVariable(parameter.ParameterType);

        public Variable FindVariableByName(Type dependency, string name)
        {
            if (TryFindVariableByName(dependency, name, out var v)) return v;
            throw new InvalidOperationException($"No known variable for {dependency} named {name}");
        }

        public bool TryFindVariableByName(Type dependency, string name, out Variable variable)
        {
            variable = _byType.Values.FirstOrDefault(x => x.Usage == name && x.VariableType == dependency)!;
            return variable != null;
        }

        public Variable? TryFindVariable(Type type, VariableSource source)
            => _byType.TryGetValue(type, out var v) ? v : null;

        public void Store(Variable variable) => _byType[variable.VariableType] = variable;
    }
}
