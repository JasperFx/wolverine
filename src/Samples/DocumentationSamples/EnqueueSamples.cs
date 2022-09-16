using Wolverine;
using TestMessages;

namespace DocumentationSamples
{
    public class EnqueueSamples
    {
        #region sample_enqueue_locally
        public static async Task enqueue_locally(ICommandBus bus)
        {
            // Enqueue a message to the local worker queues
            await bus.EnqueueAsync(new Message1());

        }

        #endregion
    }
}
