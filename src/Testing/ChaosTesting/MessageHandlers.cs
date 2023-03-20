using Marten;
using Wolverine;

namespace ChaosTesting;

public class MessageHandlers : IWolverineHandler
{
    private static Random _random = new Random();
    
    public void Handle(Tracked1 tracked, IDocumentSession session)
    {
        if (_random.Next(0, 100) < 5)
        {
            throw new DivideByZeroException("Boom!");
        }
        
        session.Delete<MessageRecord>(tracked.Id);
    }
    
    public void Handle(Tracked2 tracked, IDocumentSession session)
    {
        if (_random.Next(0, 100) < 5)
        {
            throw new BadImageFormatException("Boom!");
        }
        
        session.Delete<MessageRecord>(tracked.Id);
    }
    
    public void Handle(Tracked3 tracked, IDocumentSession session)
    {
        session.Delete<MessageRecord>(tracked.Id);
    }
    
    public void Handle(Tracked4 tracked, IDocumentSession session)
    {
        session.Delete<MessageRecord>(tracked.Id);
    }
    
    public void Handle(Tracked5 tracked, IDocumentSession session)
    {
        if (_random.Next(0, 100) < 3)
        {
            throw new DivideByZeroException("Boom!");
        }
        
        session.Delete<MessageRecord>(tracked.Id);
    }
}