using System.Diagnostics.CodeAnalysis;
using JasperFx.Core.Reflection;
using Wolverine.ErrorHandling.Matches;

namespace Wolverine.ErrorHandling;

public static class ErrorHandlingPolicyExtensions
{
    /// <summary>
    ///     Specifies the type of exception that this policy can handle.
    /// </summary>
    /// <typeparam name="TException">The type of the exception to handle.</typeparam>
    /// <returns>The PolicyBuilder instance.</returns>
    public static PolicyExpression OnException<TException>(this IWithFailurePolicies policies)
        where TException : Exception
    {
        return new PolicyExpression(policies.Failures, new TypeMatch<TException>());
    }

    /// <summary>
    ///     Apply this rule on all exceptions
    /// </summary>
    /// <param name="policies"></param>
    /// <returns></returns>
    public static PolicyExpression OnAnyException(this IWithFailurePolicies policies)
    {
        return new PolicyExpression(policies.Failures, new AlwaysMatches());
    }

    /// <summary>
    ///     Specifies the type of exception that this policy can handle with additional filters on this exception type.
    /// </summary>
    /// <typeparam name="TException">The type of the exception.</typeparam>
    /// <param name="policies"></param>
    /// <param name="exceptionPredicate">The exception predicate to filter the type of exception this policy can handle.</param>
    /// <param name="description">Optional description of this exception filter strictly for diagnostics</param>
    /// <returns>The PolicyBuilder instance.</returns>
    public static PolicyExpression OnException(this IWithFailurePolicies policies,
        Func<Exception, bool> exceptionPredicate, string description = "User supplied")
    {
        return new PolicyExpression(policies.Failures, new UserSupplied(exceptionPredicate, description));
    }

    /// <summary>
    ///     Specifies the type of exception that this policy can handle with additional filters on this exception type.
    /// </summary>
    /// <param name="policies"></param>
    /// <param name="exceptionType">An exception type to match against</param>
    /// <returns>The PolicyBuilder instance.</returns>
    // TypeMatch<> closed over a runtime exceptionType. Same CloseAndBuildAs
    // pattern as chunks D/I/J/K/O. AOT-clean apps that need typed exception
    // filtering should prefer the strongly-typed OnException<TException>
    // overload below — the closed generic instantiation is then statically
    // known by the compiler.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "TypeMatch<> closed over runtime exception type; AOT consumers prefer the OnException<TException> typed overload. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "TypeMatch<> closed over runtime exception type; AOT consumers prefer the OnException<TException> typed overload. See AOT guide.")]
    public static PolicyExpression OnExceptionOfType(this IWithFailurePolicies policies, Type exceptionType)
    {
        var match = typeof(TypeMatch<>).CloseAndBuildAs<IExceptionMatch>(exceptionType);
        return new PolicyExpression(policies.Failures, match);
    }

    /// <summary>
    ///     Specifies the type of exception that this policy can handle with additional filters on this exception type.
    /// </summary>
    /// <typeparam name="TException">The type of the exception.</typeparam>
    /// <param name="policies"></param>
    /// <param name="exceptionPredicate">The exception predicate to filter the type of exception this policy can handle.</param>
    /// <param name="description">Optional description of this exception filter strictly for diagnostics</param>
    /// <returns>The PolicyBuilder instance.</returns>
    public static PolicyExpression OnException<TException>(this IWithFailurePolicies policies,
        Func<TException, bool> exceptionPredicate, string description = "User supplied")
        where TException : Exception
    {
        return new PolicyExpression(policies.Failures, new UserSupplied<TException>(exceptionPredicate, description));
    }

    /// <summary>
    ///     Specifies the type of exception that this policy can handle if found as an InnerException of a regular
    ///     <see cref="Exception" />, or at any level of nesting within an <see cref="AggregateException" />.
    /// </summary>
    /// <typeparam name="TException">The type of the exception to handle.</typeparam>
    /// <returns>The PolicyBuilder instance, for fluent chaining.</returns>
    public static PolicyExpression OnInnerException<TException>(this IWithFailurePolicies policies)
        where TException : Exception
    {
        return new PolicyExpression(policies.Failures, new InnerMatch(new TypeMatch<TException>()));
    }

    /// <summary>
    ///     Specifies the type of exception that this policy can handle, with additional filters on this exception type, if
    ///     found as an InnerException of a regular <see cref="Exception" />, or at any level of nesting within an
    ///     <see cref="AggregateException" />.
    /// </summary>
    /// <param name="description">Optional description of this exception filter strictly for diagnostics</param>
    /// <typeparam name="TException">The type of the exception to handle.</typeparam>
    /// <returns>The PolicyBuilder instance, for fluent chaining.</returns>
    public static PolicyExpression OnInnerException<TException>(this IWithFailurePolicies policies,
        Func<TException, bool> exceptionPredicate, string description = "User supplied filter")
        where TException : Exception
    {
        return new PolicyExpression(policies.Failures,
            new InnerMatch(new UserSupplied<TException>(exceptionPredicate, description)));
    }
}