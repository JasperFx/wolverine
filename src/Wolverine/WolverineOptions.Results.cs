using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Runtime.Handlers;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Custom <c>Result&lt;T&gt;</c>-style types registered for Wolverine's railway-programming
    /// support. Populated via the <c>UseResultType&lt;TResult&gt;(...)</c> and
    /// <c>UseResultType(typeof(Result&lt;&gt;), ...)</c> entry points. Consulted by the codegen
    /// seams (continuation strategy, return-action source) at handler-chain compile and by the
    /// caller-side <c>InvokeAsync&lt;T&gt;</c> unwrap at runtime. See GH-2221.
    /// </summary>
    public ResultTypeRegistry ResultTypes { get; } = new();

    /// <summary>
    /// Register a closed Result type so Wolverine knows how to (a) treat it as an early-return
    /// signal from middleware/filter methods, (b) unwrap its success payload for cascading and
    /// for the caller-side <see cref="IMessageBus.InvokeAsync{T}" /> response, and (c) surface its
    /// failure as a <see cref="ResultFailureException" /> to callers awaiting the unwrapped type.
    /// </summary>
    /// <typeparam name="TResult">The Result type. For most users this is a concrete generic
    /// closure (e.g. <c>FluentResults.Result&lt;OrderPlaced&gt;</c>) — use the
    /// <see cref="UseResultType(Type, Func{object, bool}, Func{object, object?}, Func{object, IEnumerable{string}}, int)" />
    /// overload below to register an open-generic definition like <c>typeof(Result&lt;&gt;)</c>
    /// that covers every closed form with one entry.</typeparam>
    public ResultTypeRegistration<TResult> UseResultType<TResult>(
        Func<TResult, bool> stopWhen,
        Func<TResult, object?> unwrapWith,
        Func<TResult, IEnumerable<string>> errorsFrom)
    {
        var registration = new ResultTypeRegistration<TResult>(stopWhen, unwrapWith, errorsFrom);
        AddRegistration(registration);
        return registration;
    }

    /// <summary>
    /// Register a non-generic Result type that carries error messages on failure but has no
    /// success payload — e.g. <c>FluentResults.Result</c>. Suitable for handlers that return
    /// nothing on success.
    /// </summary>
    public IResultTypeRegistration UseResultType<TResult>(
        Func<TResult, bool> stopWhen,
        Func<TResult, IEnumerable<string>> errorsFrom)
    {
        var registration = ResultTypeRegistration.ForNonGeneric(stopWhen, errorsFrom);
        AddRegistration(registration);
        return registration;
    }

    /// <summary>
    /// Register an open-generic Result type — one entry covers every closed form. The lambdas
    /// take <see cref="object" /> because no single typed signature can name <c>Result&lt;&gt;</c>
    /// directly; cast inside the lambda (or to a marker base type like
    /// <c>FluentResults.IResultBase</c>) to access the relevant members.
    /// </summary>
    /// <param name="openGenericResultType">The open-generic type definition, e.g.
    /// <c>typeof(FluentResults.Result&lt;&gt;)</c>.</param>
    /// <param name="unwrappedArgumentIndex">Which generic-argument slot holds the success payload
    /// type. Defaults to 0 — <c>Result&lt;T&gt;</c>, <c>OneOf&lt;T, …&gt;</c>, etc. Override only
    /// for unusual layouts.</param>
    public IResultTypeRegistration UseResultType(
        Type openGenericResultType,
        Func<object, bool> stopWhen,
        Func<object, object?> unwrapWith,
        Func<object, IEnumerable<string>> errorsFrom,
        int unwrappedArgumentIndex = 0)
    {
        var registration = ResultTypeRegistration.ForOpenGeneric(openGenericResultType, stopWhen,
            unwrapWith, errorsFrom, unwrappedArgumentIndex);
        AddRegistration(registration);
        return registration;
    }

    /// <summary>
    /// Key under which <see cref="ResultTypes" /> is stashed on
    /// <see cref="GenerationRules.Properties" /> so the rules-aware continuation strategy
    /// (<c>ResultTypeContinuationPolicy</c>) and the return-action policy can read it during
    /// codegen without a separate dependency-injection hop.
    /// </summary>
    internal const string ResultTypeRegistryKey = "WOLVERINE_RESULT_TYPE_REGISTRY";

    private void AddRegistration(IResultTypeRegistration registration)
    {
        var firstRegistration = !ResultTypes.HasAny;
        ResultTypes.Add(registration);

        // Stash the registry on GenerationRules so the codegen seams can find it.
        CodeGeneration.Properties[ResultTypeRegistryKey] = ResultTypes;

        // Only attach the continuation strategy / handler-policy / DI singleton once — they all
        // consult ResultTypes per-call so the same instance covers every registration added
        // afterwards.
        if (firstRegistration)
        {
            CodeGeneration.AddContinuationStrategy<ResultTypeContinuationPolicy>();

            // Phase A handler policy that swaps the chain's IReturnVariableActionSource for the
            // unwrap-and-cascade variant whenever the handler's return type matches a registered
            // Result type. Inserted at index 0 so it runs before AutoApplyTransactions and any
            // other policies that inspect chain configuration.
            RegisteredPolicies.Insert(0, new ResultTypeReturnActionPolicy());

            Services.TryAddSingleton(ResultTypes);
        }
    }
}
