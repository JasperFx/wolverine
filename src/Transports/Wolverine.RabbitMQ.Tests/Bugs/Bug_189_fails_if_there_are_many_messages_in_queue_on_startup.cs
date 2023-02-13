using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_189_fails_if_there_are_many_messages_in_queue_on_startup
{
    [Fact]
    public async Task be_able_to_start_up_with_large_number_of_messages_waiting_on_you()
    {
        var queueName = RabbitTesting.NextQueueName();
        var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.PublishAllMessages().ToRabbitQueue(queueName).SendInline();
            }).StartAsync();

        var bus = sender.Services.GetRequiredService<IMessageBus>();
        
        for (int i = 0; i < 1000; i++)
        {
            await bus.PublishAsync(new Bug189(Guid.NewGuid()));
        }

        await sender.StopAsync();

        var waiter = Bug189Handler.WaitForCompletion(500, 120000);

        using var receiver = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();
                opts.ListenToRabbitQueue(queueName).BufferedInMemory();
            }).StartAsync();

        await waiter;
    }


    public record Bug189(Guid Id);

    public static class Bug189Handler
    {
        private static TaskCompletionSource<int> _source = new TaskCompletionSource<int>();
        private static volatile int _count = 0;
        private static int _expected;

        public static Task WaitForCompletion(int count, int millisecondTimeout)
        {
            _expected = count;

            return _source.Task.TimeoutAfterAsync(millisecondTimeout);
        }

        public static void Handle(Bug189 bug)
        {
            _count++;

            if (_count >= _expected)
            {
                _source.SetResult(_count);
            }
        }
    }
}