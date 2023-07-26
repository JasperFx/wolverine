namespace Wolverine.Runtime.Agents;

internal class NodeMessage : ISendMyself
{
    public NodeMessage(object message, WolverineNode node)
    {
        Message = message;
        Node = node;
    }

    public object Message { get; }
    public WolverineNode Node { get; }

    public ValueTask ApplyAsync(IMessageContext context) =>
        context.EndpointFor(Node.ControlUri!).SendAsync(Message);
}

internal static class NodeMessageExtensions
{
    internal static NodeMessage ToNode(this object message, WolverineNode node)
    {
        return new NodeMessage(message, node);
    }
}