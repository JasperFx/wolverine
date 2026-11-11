using Wolverine.Configuration;

namespace Wolverine.Marten.Codegen;

/// <summary>
///     Marten's transactional session middleware. GH-5141: the frame itself, C# and F#, is Wolverine core's; this type
///     exists so that <c>Middleware.OfType&lt;CreateDocumentSessionFrame&gt;()</c> only ever finds Marten's.
/// </summary>
internal class CreateDocumentSessionFrame : Wolverine.Persistence.DocumentStores.Codegen.CreateDocumentSessionFrame
{
    public CreateDocumentSessionFrame(IChain chain) : base(chain, MartenIntegration.SessionTypes)
    {
    }
}
