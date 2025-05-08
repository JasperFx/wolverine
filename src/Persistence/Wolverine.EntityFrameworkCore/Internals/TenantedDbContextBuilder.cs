using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine.Persistence.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public class TenantedDbContextBuilder<T> : IDbContextBuilder<T> where T : DbContext
{
    private readonly ITenantedSource<string> _source;
    private readonly Action<DbContextOptionsBuilder<T>, string> _configuration;

    private readonly Func<DbContextOptions<T>, T> _constructor;
    // Going to assume that it's wolverine enabled here!
    public TenantedDbContextBuilder(ITenantedSource<string> source, Action<DbContextOptionsBuilder<T>, string> configuration)
    {
        _source = source;
        _configuration = configuration;
        var optionsType = typeof(DbContextOptions<T>);
        var ctor = typeof(T).GetConstructors().FirstOrDefault(x =>
            x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == optionsType);

        if (ctor == null)
        {
            throw new InvalidOperationException(
                $"DbContext type {typeof(T).FullNameInCode()} must have a public constructor that accepts {optionsType.FullNameInCode()} as its only argument");
        }

        var options = Expression.Parameter(optionsType);
        var callCtor = Expression.New(ctor, options);
        _constructor = Expression.Lambda<Func<DbContextOptions<T>, T>>(callCtor, options).CompileFast();
    }


    public async ValueTask<T> BuildAndEnrollAsync(MessageContext messaging, CancellationToken cancellationToken)
    {
        // TODO -- what about a default tenant?????????

        var connectionString = await _source.FindAsync(messaging.TenantId ?? StorageConstants.DefaultTenantId);
        var builder = new DbContextOptionsBuilder<T>();
        _configuration(builder, connectionString);
        var dbContext = _constructor(builder.Options);

        var transaction = new MappedEnvelopeTransaction(dbContext, messaging);
        messaging.EnlistInOutbox(transaction);

        return dbContext;
    }

    public ValueTask<T> BuildAsync(CancellationToken cancellationToken)
    {
        return BuildAsync(StorageConstants.DefaultTenantId, cancellationToken);
    }

    public async ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken)
    {
        // TODO -- what about a default tenant?????????

        var connectionString = await _source.FindAsync(tenantId ?? StorageConstants.DefaultTenantId);
        var builder = new DbContextOptionsBuilder<T>();
        _configuration(builder, connectionString);
        var dbContext = _constructor(builder.Options);

        return dbContext;
    }
}