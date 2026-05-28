using Wolverine;

namespace ChaosSender;

public interface ITrackedMessage : Wolverine.IMessage
{
    Guid Id { get; }
}

public record Tracked4(Guid Id) : ITrackedMessage;

public record Tracked3(Guid Id) : ITrackedMessage;

public record Tracked2(Guid Id) : ITrackedMessage;

public record Tracked1(Guid Id) : ITrackedMessage;

public record Tracked5(Guid Id) : ITrackedMessage;


public class MessageHandlers : IWolverineHandler
{
    private readonly IMessageRecordRepository _repository;
    private static Random _random = new Random();

    public MessageHandlers(IMessageRecordRepository repository)
    {
        _repository = repository;
    }

    public ValueTask HandleAsync(Tracked1 tracked)
    {
        if (_random.Next(0, 100) < 5)
        {
            throw new DivideByZeroException("Boom!");
        }

        return _repository.MarkDeleted(tracked.Id);
    }

    public ValueTask HandleAsync(Tracked2 tracked)
    {
        if (_random.Next(0, 100) < 5)
        {
            throw new BadImageFormatException("Boom!");
        }

        return _repository.MarkDeleted(tracked.Id);
    }

    public ValueTask HandleAsync(Tracked3 tracked)
    {
        return _repository.MarkDeleted(tracked.Id);
    }

    public ValueTask HandleAsync(Tracked4 tracked)
    {
        return _repository.MarkDeleted(tracked.Id);
    }

    public ValueTask HandleAsync(Tracked5 tracked)
    {
        if (_random.Next(0, 100) < 3)
        {
            throw new DivideByZeroException("Boom!");
        }

        return _repository.MarkDeleted(tracked.Id);
    }
}