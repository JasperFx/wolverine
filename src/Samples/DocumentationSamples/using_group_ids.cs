using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;

namespace DocumentationSamples;

public class using_group_ids
{
    #region sample_group_id_and_cascading_messages

    public static IEnumerable<object> Handle(IncomingMessage message)
    {
        yield return new Message1().WithGroupId("one");
        yield return new Message2().WithGroupId("one");

        yield return new Message3().ScheduleToGroup("one", 5.Minutes());

        // Long hand
        yield return new Message4().WithDeliveryOptions(new DeliveryOptions
        {
            GroupId = "one"
        });
    }

    #endregion
}

public record IncomingMessage;