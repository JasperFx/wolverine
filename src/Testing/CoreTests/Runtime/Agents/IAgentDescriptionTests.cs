using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Coverage for <see cref="IAgent.Description"/> — the human-readable
/// "what does this agent do" string surfaced to monitoring tools
/// (e.g. CritterWatch). Verifies the default-interface fallback for
/// implementations that don't override it, and verifies an explicit
/// override wins through.
/// </summary>
public class IAgentDescriptionTests
{
    [Fact]
    public void default_description_includes_uri_scheme_and_full_uri()
    {
        // Default-interface members are only accessible through the
        // interface, not the concrete type — so cast deliberately.
        // The default implementation derives a generic description
        // from the URI; implementations are expected to override for
        // anything more specific, but the fallback should always
        // produce a non-empty string identifying scheme and URI so a
        // monitoring tool isn't left with a blank tooltip.
        IAgent agent = new BareBonesAgent(new Uri("test-agent://default"));

        agent.Description.ShouldNotBeNullOrWhiteSpace();
        agent.Description.ShouldContain("test-agent");
        agent.Description.ShouldContain(agent.Uri.ToString());
    }

    [Fact]
    public void overridden_description_wins_over_default()
    {
        IAgent agent = new DescribedAgent(
            new Uri("test-agent://configured"),
            "Custom description for this agent");

        agent.Description.ShouldBe("Custom description for this agent");
        agent.Description.ShouldNotContain("test-agent://configured");
    }

    /// <summary>
    /// Minimal IAgent that doesn't override Description. The default
    /// interface member should provide the fallback — covers the
    /// "existing IAgent implementations stay source-compatible"
    /// guarantee.
    /// </summary>
    private sealed class BareBonesAgent : IAgent
    {
        public BareBonesAgent(Uri uri)
        {
            Uri = uri;
        }

        public Uri Uri { get; }
        public AgentStatus Status => AgentStatus.Stopped;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Agent that explicitly overrides the description — exercises
    /// the implementation-side override path that built-in agents
    /// (DurabilityAgent, ExclusiveListenerAgent, etc.) use to give
    /// CritterWatch operators agent-specific tooltip text.
    /// </summary>
    private sealed class DescribedAgent : IAgent
    {
        private readonly string _description;

        public DescribedAgent(Uri uri, string description)
        {
            Uri = uri;
            _description = description;
        }

        public Uri Uri { get; }
        public AgentStatus Status => AgentStatus.Stopped;
        public string Description => _description;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
