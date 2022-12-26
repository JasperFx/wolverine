namespace Wolverine.Runtime.Interop.MassTransit;

public interface IMassTransitInteropEndpoint
{
    Uri? MassTransitUri();
    Uri? MassTransitReplyUri();

    Uri? TranslateMassTransitToWolverineUri(Uri uri);
}