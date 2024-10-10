using System;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Models a constantly running background process within a Wolverine
///     node cluster
/// </summary>
public interface IAgent : IHostedService
{
    /// <summary>
    ///     Unique identification for this agent within the Wolverine system
    /// </summary>
    Uri Uri { get; }
    
    AgentStatus Status { get; }
}

public enum AgentStatus
{
    Started,
    Stopped,
    Paused
}