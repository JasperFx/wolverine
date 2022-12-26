namespace Wolverine;

public static class Respond
{
    /// <summary>
    ///     Send a message back to the original sender of the current message
    /// </summary>
    /// <param name="response"></param>
    /// <returns></returns>
    public static RespondToSender ToSender(object response)
    {
        return new RespondToSender(response);
    }
}

/// <summary>
///     Declares that the inner message should be sent
///     to the original sender of a processed message
/// </summary>
public class RespondToSender : ISendMyself
{
    public RespondToSender(object message)
    {
        Message = message;
    }

    /// <summary>
    ///     Inner message
    /// </summary>
    public object Message { get; }

    ValueTask ISendMyself.ApplyAsync(IMessageContext context)
    {
        return context.RespondToSenderAsync(Message);
    }

    private bool Equals(RespondToSender other)
    {
        return Message.Equals(other.Message);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((RespondToSender)obj);
    }

    public override string ToString()
    {
        return $"Respond to sender with : {Message}";
    }

    public override int GetHashCode()
    {
        return Message.GetHashCode();
    }
}