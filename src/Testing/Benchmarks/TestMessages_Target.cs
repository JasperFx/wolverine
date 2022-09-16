namespace Benchmarks_Generated
{
    public class TestMessages_Target : Wolverine.Runtime.Handlers.MessageHandler
    {


        public override System.Threading.Tasks.Task HandleAsync(Wolverine.IMessageContext context, System.Threading.CancellationToken cancellation)
        {
            var target = (TestMessages.Target)context.Envelope.Message;
            Benchmarks.TargetHandler.Handle(target);
            return System.Threading.Tasks.Task.CompletedTask;
        }

    }
}
