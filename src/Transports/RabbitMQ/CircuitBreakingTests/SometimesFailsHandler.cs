using Wolverine;

namespace CircuitBreakingTests;

public class SometimesFailsHandler
{
    public void Handle(SometimesFails message, Envelope envelope, Recorder recorder)
    {
        var result = determineResult(message, envelope, recorder);
        switch (result)
        {
            case MessageResult.BadImage:
                throw new BadImageFormatException();
            case MessageResult.DivideByZero:
                throw new DivideByZeroException();
            default:
                recorder.Increment(message.Id, envelope.Attempts);
                break;
        }
    }

    private MessageResult determineResult(SometimesFails message, Envelope envelope, Recorder recorder)
    {
        if (recorder.NeverFail)
            return MessageResult.Success;

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