using Google.Api.Gax;

namespace Wolverine.Pubsub;

public class PubsubTransportOptions {
    public EmulatorDetection EmulatorDetection = EmulatorDetection.None;
    public bool DisableDeadLetter = false;
}
