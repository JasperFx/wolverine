using Wolverine.Configuration;

namespace Wolverine.Fisher.Codegen;

/// <summary>
///     Fisher's transactional session middleware. GH-5141: the frame itself, C# and F#, is Wolverine core's; this type
///     exists so that <c>Middleware.OfType&lt;CreateDocumentSessionFrame&gt;()</c> only ever finds Fisher's.
/// </summary>
internal class CreateDocumentSessionFrame : Wolverine.Persistence.DocumentStores.Codegen.CreateDocumentSessionFrame
{
    public CreateDocumentSessionFrame(IChain chain) : base(chain, FisherIntegration.SessionTypes)
    {
    }
}
