using Alba;

namespace Wolverine.Bobcat.Http;

/// <summary>
/// <see cref="WolverineSpec" /> over an Alba host, adding the Wolverine.HTTP acts of
/// <see cref="WolverineHttpScenario" /> (GH-4834).
/// </summary>
public abstract class WolverineHttpSpec : WolverineSpec
{
    protected WolverineHttpSpec(IAlbaHost host, string? storeName = null) : this(new WolverineHttpScenario(host, storeName))
    {
    }

    protected WolverineHttpSpec(WolverineHttpScenario scenario) : base(scenario)
    {
        HttpScenario = scenario;
    }

    /// <summary>The scenario every step delegates to.</summary>
    public WolverineHttpScenario HttpScenario { get; }

    /// <inheritdoc cref="WolverineHttpScenario.AlbaHost" />
    public IAlbaHost AlbaHost => HttpScenario.AlbaHost;

    /// <inheritdoc cref="WolverineHttpScenario.LastResponse" />
    public IScenarioResult? LastResponse => HttpScenario.LastResponse;

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted(object, Action{Scenario}?)" />
    public Task WhenPosted(object command, Action<Scenario>? configure = null) => HttpScenario.WhenPosted(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted(object, string, Action{Scenario}?)" />
    public Task WhenPosted(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenPosted(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted{TResponse}(object, Action{Scenario}?)" />
    public Task<TResponse?> WhenPosted<TResponse>(object command, Action<Scenario>? configure = null)
        => HttpScenario.WhenPosted<TResponse>(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted{TResponse}(object, string, Action{Scenario}?)" />
    public Task<TResponse?> WhenPosted<TResponse>(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenPosted<TResponse>(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.ThenResponseIs" />
    public void ThenResponseIs(int status) => HttpScenario.ThenResponseIs(status);

    /// <inheritdoc cref="WolverineHttpScenario.ThenRefusedWithProblem" />
    public Task<Microsoft.AspNetCore.Mvc.ProblemDetails?> ThenRefusedWithProblem(object? expected = null)
        => HttpScenario.ThenRefusedWithProblem(expected);
}
