using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.CodeGen;

/// <summary>
///     Supplies the current UTC time for an endpoint parameter named <c>now</c>, matching the long standing
///     message handler convention documented at <c>/guide/handlers/#the-current-time</c>. The value itself comes
///     from whatever <see cref="IVariableSource" /> is registered for the parameter's type -- normally JasperFx's
///     <c>NowTimeVariableSource</c>, added to <c>WolverineOptions.CodeGeneration.Sources</c> at bootstrapping --
///     so HTTP and message handlers cannot drift, and a custom clock registered as a variable source is honored
///     by both.
/// </summary>
/// <remarks>
///     <para>
///         Gated on the parameter <b>name</b> on purpose. <see cref="IVariableSource.Matches" /> takes only a
///         <see cref="Type" />, and in an HTTP endpoint a bare <c>DateTimeOffset</c> is an entirely ordinary query
///         string parameter -- <c>DateTimeOffset from</c>, <c>DateTimeOffset to</c>, <c>DateTimeOffset asOf</c>.
///         Applying the variable source by type alone would silently hand those endpoints <c>UtcNow</c> instead of
///         the caller's value, with nothing to notice at compile time or run time. A message handler has no such
///         competition, which is why the type-only match is safe there and not here.
///     </para>
///     <para>
///         Registered directly after <see cref="RouteParameterStrategy" /> so that an explicit <c>{now}</c> route
///         argument still wins, as do the explicit <c>[FromRoute]</c> / <c>[FromQuery]</c> / <c>[FromHeader]</c>
///         attribute strategies, which run earlier still.
///     </para>
/// </remarks>
internal class CurrentTimeParameterStrategy : IParameterStrategy
{
    private readonly GenerationRules _rules;

    public CurrentTimeParameterStrategy(GenerationRules rules)
    {
        _rules = rules;
    }

    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter,
        out Variable? variable)
    {
        if (!string.Equals(parameter.Name, "now", StringComparison.OrdinalIgnoreCase))
        {
            variable = null;
            return false;
        }

        foreach (var source in _rules.Sources)
        {
            if (source.Matches(parameter.ParameterType))
            {
                variable = source.Create(parameter.ParameterType);
                return true;
            }
        }

        variable = null;
        return false;
    }
}
