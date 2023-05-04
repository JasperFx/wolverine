namespace Wolverine.Runtime.Agents;

public class AgentCommandException : Exception
{
    public AgentCommandException(IAgentCommand command, Exception? innerException) : base($"Failure while executing agent command {command}", innerException)
    {
    }
}