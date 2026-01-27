using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;

namespace DocumentationSamples;

public interface IControlMessage;

public class ConcurrencyExamples
{
    public static async Task configure_strict_ordering()
    {
        #region sample_using_strict_ordering_for_control_queue

        var builder = Host.CreateApplicationBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();

                // Wolverine will *only* listen to this queue
                // on one single node and process messages in strict
                // order
                opts.ListenToRabbitQueue("control").ListenWithStrictOrdering();

                opts.Publish(x =>
                {
                    // Just keying off a made up marker interface
                    x.MessagesImplementing<IControlMessage>();
                    x.ToRabbitQueue("control");
                });
            });

        #endregion
    }
}