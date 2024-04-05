using Marten;

namespace ChaosSender;

public record SendMessages(int Number);

public class SendMessageHandler
{
    private static T send<T>(IDocumentSession session) where T : ITrackedMessage
    {
        var message = (T)Activator.CreateInstance(typeof(T), Guid.NewGuid())!;
        var record = MessageRecord.For(message);
        session.Store(record);

        return message;
    }

    public static IEnumerable<object> Handle(SendMessages command, IDocumentSession session)
    {
        var count = 0;
        while (true)
        {
            yield return send<Tracked1>(session);
            count++;
            if (count >= command.Number) break;

            yield return send<Tracked2>(session);
            count++;
            if (count >= command.Number) break;

            yield return send<Tracked3>(session);
            count++;
            if (count >= command.Number) break;

            yield return send<Tracked4>(session);
            count++;
            if (count >= command.Number) break;

            yield return send<Tracked5>(session);
            count++;
            if (count >= command.Number) break;
        }
    }
}