using Wolverine;

namespace CircuitBreakingTests;

public class SometimesFailsHandler
{
    public void Handle(SometimesFails message, Envelope envelope)
    {
        var result = determineResult(message, envelope);
        switch (result)
        {
            case MessageResult.BadImage:
                throw new BadImageFormatException();
            case MessageResult.DivideByZero:
                throw new DivideByZeroException();
            default:
                Recorder.Increment();
                break;
        }
    }

    private MessageResult determineResult(SometimesFails message, Envelope envelope)
    {
        if (Recorder.NeverFail)
        {
            return MessageResult.Success;
        }

        switch (envelope.Attempts)
        {
            case 0:
            case 1:
                return message.First;
            case 2:
                return message.Second;
            case 3:
                return message.Third;
            default:
                return MessageResult.Success;
        }
    }


}
