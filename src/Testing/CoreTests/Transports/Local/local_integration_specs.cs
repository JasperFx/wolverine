using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Shouldly;
using TestMessages;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Transports.Local;

public class local_integration_specs : IntegrationContext
{
    public local_integration_specs(DefaultApp @default) : base(@default)
    {
    }

    private void configure()
    {
        with(opts =>
        {
            opts.Publish(x => x.Message<Message1>()
                .ToLocalQueue("incoming"));

            #region sample_opting_into_STJ

            opts.UseSystemTextJsonForSerialization(stj =>
            {
                stj.UnknownTypeHandling = JsonUnknownTypeHandling.JsonNode;
            });

            #endregion
        });
    }


    [Fact]
    public async Task send_a_message_and_get_the_response()
    {
        configure();

        var message1 = new Message1();
        var session = await Host.SendMessageAndWaitAsync(message1, timeoutInMilliseconds: 15000);


        session.FindSingleTrackedMessageOfType<Message1>(EventType.MessageSucceeded)
            .ShouldBeSameAs(message1);
    }
}
