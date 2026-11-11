using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.DocumentStores.Codegen;

namespace Wolverine.Persistence.DocumentStores;

/// <summary>
///     GH-5145: the one implementation behind Marten's, Polecat's and Fisher's
///     <c>[DocumentExists&lt;T&gt;]</c> and <c>[DocumentDoesNotExist&lt;T&gt;]</c>. Each store's attribute
///     differs only by its <see cref="DocumentSessionTypes" /> and its persistence frame provider.
/// </summary>
internal static class DocumentExistenceRequirement
{
    /// <summary>
    ///     Add the existence check for <paramref name="documentType" /> to the chain.
    /// </summary>
    /// <param name="provider">
    ///     The store's own provider. The id type comes from its chain-aware
    ///     <see cref="IPersistenceFrameProvider.DetermineSagaIdType(Type, IChain, IServiceContainer)" />, so a
    ///     chain routed to an ancillary store asks that store rather than the default one.
    /// </param>
    public static void Apply(IChain chain, IServiceContainer container, IPersistenceFrameProvider provider,
        DocumentSessionTypes sessionTypes, Type documentType, string? argumentName, bool mustExist)
    {
        var idType = provider.DetermineSagaIdType(documentType, chain, container);

        if (!TryFindIdentityVariable(chain, argumentName, documentType, idType, out var identity))
        {
            throw new InvalidOperationException(
                $"Could not find an identity variable for {documentType.FullNameInCode()} on chain {chain}. " +
                $"Expected a property named '{documentType.Name}Id' or 'Id' on the input type, " +
                $"or specify the property name explicitly in the attribute constructor.");
        }

        if (identity.Creator != null)
        {
            chain.Middleware.Add(identity.Creator);
        }

        chain.Middleware.Add(new DocumentExistenceCheckFrame(sessionTypes, documentType, identity, mustExist));
    }

    internal static bool TryFindIdentityVariable(IChain chain, string? argumentName, Type docType, Type idType,
        out Variable variable)
    {
        if (argumentName.IsNotEmpty())
        {
            if (chain.TryFindVariable(argumentName, ValueSource.Anything, idType, out variable))
            {
                return true;
            }
        }

        if (chain.TryFindVariable(docType.Name + "Id", ValueSource.Anything, idType, out variable))
        {
            return true;
        }

        if (chain.TryFindVariable("Id", ValueSource.Anything, idType, out variable))
        {
            return true;
        }

        variable = default!;
        return false;
    }
}

/// <summary>
///     Checks whether a document exists (or does not exist) and throws
///     <see cref="RequiredDataMissingException" /> on failure.
/// </summary>
/// <remarks>
///     The generated call is <c>CheckExistsAsync&lt;T&gt;(id, token)</c> on the store's <b>own</b> session type,
///     not on JasperFx's <c>IDocumentReadOperations</c>. Marten's session binds it to
///     <c>CheckExistsAsync&lt;T&gt;(object)</c>, the member JasperFx's contract declares; Polecat's and Fisher's
///     bind to their typed overloads, which answer with an existence query instead of the contract's default
///     load. Asking for the JasperFx contract would also pick whichever store registered its codegen source
///     first in a host with more than one store.
/// </remarks>
internal class DocumentExistenceCheckFrame : AsyncFrame
{
    private static int _count;

    private readonly DocumentSessionTypes _sessionTypes;
    private readonly Type _docType;
    private readonly Variable _identity;
    private readonly bool _mustExist;
    private readonly int _id;

    private Variable? _session;
    private Variable? _logger;
    private Variable? _cancellation;

    public DocumentExistenceCheckFrame(DocumentSessionTypes sessionTypes, Type docType, Variable identity,
        bool mustExist)
    {
        _sessionTypes = sessionTypes;
        _docType = docType;
        _identity = identity;
        _mustExist = mustExist;
        _id = Interlocked.Increment(ref _count);
        uses.Add(identity);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(_sessionTypes.DocumentSession);
        yield return _session;

        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var docTypeName = _docType.FullNameInCode();
        var existsVar = $"docExists{_id}";

        writer.WriteComment(_mustExist
            ? $"Verify that {docTypeName} exists"
            : $"Verify that {docTypeName} does not exist");

        writer.WriteLine(
            $"var {existsVar} = await {_session!.Usage}.CheckExistsAsync<{docTypeName}>({_identity.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");

        var msg = _mustExist
            ? $"No {_docType.Name} with the specified identity exists"
            : $"A {_docType.Name} with the specified identity already exists";

        writer.Write(_mustExist ? $"BLOCK:if (!{existsVar})" : $"BLOCK:if ({existsVar})");
        writer.WriteLine(
            $"{_logger!.Usage}.LogWarning(\"{_sessionTypes.StoreName} data requirement failure: {{Message}}\", \"{msg}\");");
        writer.WriteLine(
            $"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(\"{msg}\");");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }
}
