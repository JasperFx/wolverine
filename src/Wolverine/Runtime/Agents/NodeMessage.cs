namespace Wolverine.Runtime.Agents;

internal class NodeMessage : ISendMyself
{
    public object Message { get; }
    public WolverineNode Node { get; }

    public NodeMessage(object message, WolverineNode node)
    {
        Message = message;
        Node = node;
    }

    public async ValueTask ApplyAsync(IMessageContext context)
    {
        await context.EndpointFor(Node.ControlUri).SendAsync(Message);
    }
}

internal static class NodeMessageExtensions
{
    internal static NodeMessage ToNode(this object message, WolverineNode node)
    {
        return new NodeMessage(message, node);
    }
}