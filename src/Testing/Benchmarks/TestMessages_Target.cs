using System.Threading;
using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Benchmarks_Generated
{
    public class TestMessages_Target : Wolverine.Runtime.Handlers.MessageHandler
    {


        public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
        {
            var target = (TestMessages.Target)context.Envelope.Message;
            Benchmarks.TargetHandler.Handle(target);
            return System.Threading.Tasks.Task.CompletedTask;
        }

    }
}
