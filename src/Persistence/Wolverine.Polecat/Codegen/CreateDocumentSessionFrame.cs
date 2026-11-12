using Wolverine.Configuration;

namespace Wolverine.Polecat.Codegen;

/// <summary>
///     Polecat's transactional session middleware. GH-5141: the frame itself, C# and F#, is Wolverine core's; this type
///     exists so that <c>Middleware.OfType&lt;CreateDocumentSessionFrame&gt;()</c> only ever finds Polecat's.
/// </summary>
internal class CreateDocumentSessionFrame : Wolverine.Persistence.DocumentStores.Codegen.CreateDocumentSessionFrame
{
    public CreateDocumentSessionFrame(IChain chain) : base(chain, PolecatIntegration.SessionTypes)
    {
    }
}
