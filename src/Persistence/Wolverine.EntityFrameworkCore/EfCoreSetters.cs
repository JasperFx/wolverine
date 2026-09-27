using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
///     Collects the column assignments of an <see cref="EfCoreOps.ExecuteUpdate{T}" />, spelled the
///     same way EF Core spells them.
/// </summary>
/// <remarks>
///     <para>
///     Wolverine records the setters rather than handing EF Core's own builder straight through, for
///     two reasons. EF Core 9 takes an <c>Expression&lt;Func&lt;SetPropertyCalls&lt;T&gt;, ...&gt;&gt;</c>
///     and EF Core 10 replaced it with an <c>Action&lt;UpdateSettersBuilder&lt;T&gt;&gt;</c>, and
///     neither type exists on the other version -- so a single public signature has to be Wolverine's
///     own. And a recorded setter can be READ: that is what lets conjoined multi-tenancy refuse an
///     update that would set <c>TenantId</c> before the statement is ever composed.
///     </para>
/// </remarks>
public sealed class EfCoreSetters<T> where T : class
{
    private readonly List<IEfCoreSetter<T>> _setters = [];

    internal IReadOnlyList<IEfCoreSetter<T>> Setters => _setters;

    /// <summary>
    ///     Set a column to a constant value.
    /// </summary>
    public EfCoreSetters<T> SetProperty<TProperty>(Expression<Func<T, TProperty>> property, TProperty value)
    {
        ArgumentNullException.ThrowIfNull(property);

        _setters.Add(new EfCoreSetter<T, TProperty>(property, value));
        return this;
    }

    /// <summary>
    ///     Set a column to a value computed from the row itself, e.g.
    ///     <c>SetProperty(x =&gt; x.Tally, x =&gt; x.Tally + 1)</c>.
    /// </summary>
    public EfCoreSetters<T> SetProperty<TProperty>(Expression<Func<T, TProperty>> property,
        Expression<Func<T, TProperty>> value)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);

        _setters.Add(new EfCoreSetter<T, TProperty>(property, value));
        return this;
    }

#if NET10_0_OR_GREATER
    internal Action<UpdateSettersBuilder<T>> ToEfCoreSetters()
    {
        var setters = _setters;
        return builder =>
        {
            foreach (var setter in setters) setter.ApplyTo(builder);
        };
    }
#else
    internal Expression<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>> ToEfCoreSetters()
    {
        var parameter = Expression.Parameter(typeof(SetPropertyCalls<T>), "s");

        Expression body = parameter;
        foreach (var setter in _setters) body = setter.Chain(body);

        return Expression.Lambda<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>>(body, parameter);
    }
#endif
}

/// <summary>
///     One recorded column assignment. Internal, but the property lambda is readable so the conjoined
///     tenancy rules can see what the update touches.
/// </summary>
internal interface IEfCoreSetter<T> where T : class
{
    LambdaExpression Property { get; }

#if NET10_0_OR_GREATER
    void ApplyTo(UpdateSettersBuilder<T> builder);
#else
    Expression Chain(Expression current);
#endif
}

// AOT note (#2746): T is an entity type mapped in a registered DbContext and TProperty one of its
// mapped property types, so the EF Core model roots both; the expression built below is handed to
// EF's query translator and never compiled to a delegate.
[UnconditionalSuppressMessage("Trimming", "IL2060",
    Justification = "SetPropertyCalls<T>.SetProperty closed over a mapped property type, which the EF Core model roots. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "SetPropertyCalls<T>.SetProperty closed over a mapped property type, which the EF Core model roots. See AOT guide / #2755.")]
internal sealed class EfCoreSetter<T, TProperty> : IEfCoreSetter<T> where T : class
{
    private readonly Expression<Func<T, TProperty>> _property;
    private readonly TProperty _value;
    private readonly Expression<Func<T, TProperty>>? _valueExpression;

    public EfCoreSetter(Expression<Func<T, TProperty>> property, TProperty value)
    {
        _property = property;
        _value = value;
    }

    public EfCoreSetter(Expression<Func<T, TProperty>> property, Expression<Func<T, TProperty>> valueExpression)
    {
        _property = property;
        _value = default!;
        _valueExpression = valueExpression;
    }

    public LambdaExpression Property => _property;

#if NET10_0_OR_GREATER
    public void ApplyTo(UpdateSettersBuilder<T> builder)
    {
        if (_valueExpression != null)
        {
            builder.SetProperty(_property, _valueExpression);
        }
        else
        {
            builder.SetProperty(_property, _value);
        }
    }
#else
    // EF Core 9's SetPropertyCalls<T>.SetProperty takes Func<T, TProperty> rather than
    // Expression<Func<T, TProperty>>, because the whole chain is itself inside an expression tree --
    // so the lambda NODES go straight in as arguments, unquoted.
    private static readonly MethodInfo _setConstant = typeof(SetPropertyCalls<T>)
        .GetMethods()
        .Single(x => x.Name == nameof(SetPropertyCalls<T>.SetProperty)
                     && x.GetParameters()[1].ParameterType.IsGenericParameter);

    private static readonly MethodInfo _setComputed = typeof(SetPropertyCalls<T>)
        .GetMethods()
        .Single(x => x.Name == nameof(SetPropertyCalls<T>.SetProperty)
                     && !x.GetParameters()[1].ParameterType.IsGenericParameter);

    public Expression Chain(Expression current)
    {
        if (_valueExpression != null)
        {
            return Expression.Call(current, _setComputed.MakeGenericMethod(typeof(TProperty)), _property,
                _valueExpression);
        }

        return Expression.Call(current, _setConstant.MakeGenericMethod(typeof(TProperty)), _property,
            Expression.Constant(_value, typeof(TProperty)));
    }
#endif
}
