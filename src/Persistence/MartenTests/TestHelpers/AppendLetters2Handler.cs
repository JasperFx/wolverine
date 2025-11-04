using Wolverine;
using Wolverine.Marten;

namespace MartenTests.TestHelpers;

public static class AppendLetters2Handler
{
    [MartenStore(typeof(ILetterStore))]
    public static (Events, OutgoingMessages) Handle(
        AppendLetters2 command, 
        
        [WriteAggregate(Required = false)]
        LetterCounts aggregate)
    {
        switch (command.Events.Length)
        {
            case 0:
                return ([], []);
            
            case 1: 
                return (new Events(command.Events[0].ToLetterEvents()), []);
            
            default:
                return (new Events(command.Events[0].ToLetterEvents()), [new AppendLetters2(command.Id, command.Events.Skip(1).ToArray())]);
        }
    }
}