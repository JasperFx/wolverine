using System.Threading;
using System.Threading.Tasks;
using Benchmarks;
using TestMessages;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Benchmarks_Generated;

public class TestMessages_Target : MessageHandler
{
    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var target = (Target)context.Envelope.Message;
        TargetHandler.Handle(target);
        return Task.CompletedTask;
    }
}