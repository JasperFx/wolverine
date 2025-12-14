using Wolverine;
using Wolverine.Marten;

namespace MartenTests.TestHelpers;

public static class AppendLettersHandler
{
    public static (Events, OutgoingMessages) Handle(
        AppendLetters command, 
        
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
                return (new Events(command.Events[0].ToLetterEvents()), [new AppendLetters(command.Id, command.Events.Skip(1).ToArray())]);
        }
    }
}