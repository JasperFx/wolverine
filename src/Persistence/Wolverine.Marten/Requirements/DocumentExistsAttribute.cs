using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;

namespace Wolverine.Marten.Requirements;

/// <summary>
/// Apply to a handler or HTTP endpoint method to declaratively check that a Marten document
/// of type <typeparamref name="TDoc"/> exists before the handler executes. Throws
/// <see cref="RequiredDataMissingException"/> if the document is not found.
///
/// The identity is resolved from the message/request by looking for a property named
/// <c>{DocTypeName}Id</c> or <c>Id</c> on the input type. You can override this by
/// specifying the property name explicitly.
/// </summary>
/// <example>
/// <code>
/// // Convention: looks for UserId or Id on the command
/// [DocumentExists&lt;User&gt;]
/// public void Handle(PromoteUser command) { }
///
/// // Explicit property name
/// [DocumentExists&lt;User&gt;(nameof(AddUser.UserId))]
/// public void Handle(AddUser command) { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class DocumentExistsAttribute<TDoc> : ModifyChainAttribute where TDoc : class
{
    private readonly string? _argumentName;

    public DocumentExistsAttribute()
    {
    }

    public DocumentExistsAttribute(string argumentName)
    {
        _argumentName = argumentName;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        var store = container.GetInstance<IDocumentStore>();
        var documentType = store.Options.FindOrResolveDocumentType(typeof(TDoc));
        var idType = documentType.IdType;

        if (!TryFindIdentityVariable(chain, _argumentName, typeof(TDoc), idType, out var identity))
        {
            throw new InvalidOperationException(
                $"Could not find an identity variable for {typeof(TDoc).FullNameInCode()} on chain {chain}. " +
                $"Expected a property named '{typeof(TDoc).Name}Id' or 'Id' on the input type, " +
                $"or specify the property name explicitly in the attribute constructor.");
        }

        if (identity.Creator != null)
        {
            chain.Middleware.Add(identity.Creator);
        }

        chain.Middleware.Add(new DocumentExistenceCheckFrame(typeof(TDoc), idType, identity, mustExist: true));
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
/// Apply to a handler or HTTP endpoint method to declaratively check that a Marten document
/// of type <typeparamref name="TDoc"/> does NOT exist before the handler executes. Throws
/// <see cref="RequiredDataMissingException"/> if the document already exists.
///
/// The identity is resolved from the message/request by looking for a property named
/// <c>{DocTypeName}Id</c> or <c>Id</c> on the input type. You can override this by
/// specifying the property name explicitly.
/// </summary>
/// <example>
/// <code>
/// // Convention: looks for UserId or Id on the command
/// [DocumentDoesNotExist&lt;User&gt;]
/// public void Handle(CreateUser command) { }
///
/// // Explicit property name
/// [DocumentDoesNotExist&lt;User&gt;(nameof(CreateUser.Email))]
/// public void Handle(CreateUser command) { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class DocumentDoesNotExistAttribute<TDoc> : ModifyChainAttribute where TDoc : class
{
    private readonly string? _argumentName;

    public DocumentDoesNotExistAttribute()
    {
    }

    public DocumentDoesNotExistAttribute(string argumentName)
    {
        _argumentName = argumentName;
    }

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        var store = container.GetInstance<IDocumentStore>();
        var documentType = store.Options.FindOrResolveDocumentType(typeof(TDoc));
        var idType = documentType.IdType;

        if (!DocumentExistsAttribute<TDoc>.TryFindIdentityVariable(chain, _argumentName, typeof(TDoc), idType,
                out var identity))
        {
            throw new InvalidOperationException(
                $"Could not find an identity variable for {typeof(TDoc).FullNameInCode()} on chain {chain}. " +
                $"Expected a property named '{typeof(TDoc).Name}Id' or 'Id' on the input type, " +
                $"or specify the property name explicitly in the attribute constructor.");
        }

        if (identity.Creator != null)
        {
            chain.Middleware.Add(identity.Creator);
        }

        chain.Middleware.Add(new DocumentExistenceCheckFrame(typeof(TDoc), idType, identity, mustExist: false));
    }
}

/// <summary>
/// Code generation frame that checks whether a Marten document exists (or does not exist)
/// and throws <see cref="RequiredDataMissingException"/> on failure.
/// </summary>
internal class DocumentExistenceCheckFrame : AsyncFrame
{
    private static int _count;

    private readonly Type _docType;
    private readonly Type _idType;
    private readonly Variable _identity;
    private readonly bool _mustExist;
    private readonly int _id;

    private Variable? _session;
    private Variable? _logger;
    private Variable? _cancellation;

    public DocumentExistenceCheckFrame(Type docType, Type idType, Variable identity, bool mustExist)
    {
        _docType = docType;
        _idType = idType;
        _identity = identity;
        _mustExist = mustExist;
        _id = ++_count;
        uses.Add(identity);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var docTypeName = _docType.FullNameInCode();
        var idTypeName = _idType.FullNameInCode();
        var existsVar = $"docExists{_id}";

        writer.WriteComment(_mustExist
            ? $"Verify that {docTypeName} exists"
            : $"Verify that {docTypeName} does not exist");

        writer.WriteLine(
            $"var {existsVar} = await {_session!.Usage}.CheckExistsAsync<{docTypeName}>({_identity.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");

        if (_mustExist)
        {
            writer.Write($"BLOCK:if (!{existsVar})");
            var msg = $"No {_docType.Name} with the specified identity exists";
            writer.WriteLine(
                $"{_logger!.Usage}.LogWarning(\"Marten data requirement failure: {{Message}}\", \"{msg}\");");
            writer.WriteLine(
                $"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(\"{msg}\");");
            writer.FinishBlock();
        }
        else
        {
            writer.Write($"BLOCK:if ({existsVar})");
            var msg = $"A {_docType.Name} with the specified identity already exists";
            writer.WriteLine(
                $"{_logger!.Usage}.LogWarning(\"Marten data requirement failure: {{Message}}\", \"{msg}\");");
            writer.WriteLine(
                $"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(\"{msg}\");");
            writer.FinishBlock();
        }

        Next?.GenerateCode(method, writer);
    }
}
