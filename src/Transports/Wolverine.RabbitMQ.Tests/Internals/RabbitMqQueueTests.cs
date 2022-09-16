using System.Linq;
using Baseline.Dates;
using Baseline.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals
{
    public class RabbitMqQueueTests
    {
        [Fact]
        public void defaults()
        {
            var queue = new RabbitMqQueue("foo");

            queue.Name.ShouldBe("foo");
            queue.IsDurable.ShouldBeTrue();
            queue.IsExclusive.ShouldBeFalse();
            queue.AutoDelete.ShouldBeFalse();
            queue.Arguments.Any().ShouldBeFalse();
        }

        [Fact]
        public void set_time_to_live()
        {
            var queue = new RabbitMqQueue("foo");
            queue.TimeToLive(3.Minutes());
            queue.Arguments["x-message-ttl"].ShouldBe(180000);
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        public void declare(bool autoDelete, bool isExclusive, bool isDurable)
        {
            var queue = new RabbitMqQueue("foo")
            {
                AutoDelete = autoDelete,
                IsExclusive = isExclusive,
                IsDurable = isDurable,
            };

            queue.HasDeclared.ShouldBeFalse();

            var channel = Substitute.For<IModel>();
            queue.Declare(channel, NullLogger.Instance);

            channel.Received()
                .QueueDeclare("foo", queue.IsDurable, queue.IsExclusive, queue.AutoDelete, queue.Arguments);

            queue.HasDeclared.ShouldBeTrue();
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        public void declare_second_time(bool autoDelete, bool isExclusive, bool isDurable)
        {
            var queue = new RabbitMqQueue("foo")
            {
                AutoDelete = autoDelete,
                IsExclusive = isExclusive,
                IsDurable = isDurable,
            };

            // cheating here.
            var prop = ReflectionHelper.GetProperty<RabbitMqQueue>(x => x.HasDeclared);
            prop.SetValue(queue, true);

            var channel = Substitute.For<IModel>();
            queue.Declare(channel, NullLogger.Instance);

            channel.DidNotReceiveWithAnyArgs().QueueDeclare("foo", isDurable, isExclusive, autoDelete, queue.Arguments);
            queue.HasDeclared.ShouldBeTrue();
        }

        [Fact]
        public void purge_messages_on_first_usage()
        {

        }
    }
}
