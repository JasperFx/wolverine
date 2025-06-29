using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;

namespace Wolverine.Runtime.Agents;

public enum AgentRestrictionType
{
    Pinned,
    Paused,
    None
}

public record AgentRestriction(Guid Id, Uri AgentUri, AgentRestrictionType Type, int NodeNumber);

public class AgentRestrictions
{
    private readonly AgentRestriction[] _originals;
    private readonly List<AgentRestriction> _current;


    public AgentRestrictions(AgentRestriction[] restrictions)
    {
        _originals = restrictions;
        _current = [..restrictions];
    }

    public IReadOnlyList<AgentRestriction> Current => _current;

    public IEnumerable<AgentRestriction> FindPinnedAgents()
    {
        return _current.Where(x => x.Type == AgentRestrictionType.Pinned);
    }

    public IEnumerable<Uri> FindPausedAgentUris()
    {
        return _current.Where(x => x.Type == AgentRestrictionType.Paused).Select(x => x.AgentUri);
    }

    public void PinAgent(Uri agentUri, int nodeNumber)
    {
        var candidate = _current.FirstOrDefault(x => x.AgentUri == agentUri && x.Type == AgentRestrictionType.Pinned);

        if (candidate == null)
        {
            var restriction = new AgentRestriction(CombGuidIdGeneration.NewGuid(), agentUri,
                AgentRestrictionType.Pinned, nodeNumber);
            _current.Add(restriction);
        }
        else if (candidate.NodeNumber != nodeNumber)
        {
            var restriction = candidate with { NodeNumber = nodeNumber };
            _current.Remove(candidate);
            _current.Add(restriction);
        }
    }

    public void PauseAgent(Uri agentUri)
    {
        var candidate = _current
                            .FirstOrDefault(x => x.AgentUri == agentUri && x.Type == AgentRestrictionType.Paused)
                        ?? _current.FirstOrDefault(x =>
                            x.AgentUri == agentUri && x.Type == AgentRestrictionType.None && x.NodeNumber == 0);

        if (candidate == null)
        {
            var restriction =
                new AgentRestriction(CombGuidIdGeneration.NewGuid(), agentUri, AgentRestrictionType.Paused, 0);
            
            _current.Add(restriction);
            return;
        }

        var restriction2 = candidate with { Type = AgentRestrictionType.Paused };
        _current.Remove(candidate);
        _current.Add(restriction2);
    }

    public void RestartAgent(Uri agentUri)
    {
        var candidate = _current.FirstOrDefault(x => x.AgentUri == agentUri && x.Type == AgentRestrictionType.Paused);
        if (candidate != null)
        {
            var restriction = candidate with { Type = AgentRestrictionType.None };
            _current.Remove(candidate);
            _current.Add(restriction);
        }
    }

    public IReadOnlyList<AgentRestriction> FindChanges()
    {
        return _current.Where(x => !_originals.Contains(x)).ToList();
    }

    public IReadOnlyList<AgentRestriction> Pins() =>
        _current.Where(x => x.Type == AgentRestrictionType.Pinned).ToList();

    public void RemovePin(Uri agentUri)
    {
        var current = _current.FirstOrDefault(x => x.AgentUri == agentUri && x.Type == AgentRestrictionType.Pinned);
        if (current == null) return; // Miss, just get out of here
        
        var replaced = current with { Type = AgentRestrictionType.None, NodeNumber = 0 };
        _current.Remove(current);
        _current.Add(replaced);
    }

    public void MergeChanges(AgentRestrictions other)
    {
        throw new NotImplementedException();
    }

    public bool HasAnyDifferencesFrom(AgentRestriction[] serviceRestrictions)
    {
        throw new NotImplementedException();
    }
}