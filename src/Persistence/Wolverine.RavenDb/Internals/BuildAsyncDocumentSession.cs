using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Wolverine.Runtime;
using MethodCall = JasperFx.CodeGeneration.Frames.MethodCall;

namespace Wolverine.RavenDb.Internals;

internal class AsyncDocumentSessionSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IAsyncDocumentSession);
    }

    public Variable Create(Type type)
    {
        return new BuildAsyncDocumentSession().Creates.Single();
    }
}

internal class BuildAsyncDocumentSession : MethodCall
{
    private Variable? _context;
    private bool _enlistInOutbox;
    private bool _useOptimisticConcurrency;

    public BuildAsyncDocumentSession() : base(typeof(IDocumentStore), ReflectionHelper.GetMethod<IDocumentStore>(x => x.OpenAsyncSession())!)
    {
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in base.FindVariables(chain)) yield return variable;

        // GH-5678. Resolved here whether or not the session ends up enlisted, so the message context is declared
        // ahead of the session. The transactional frame may ask for the enlistment before or after this runs
        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);
        if (_context != null) yield return _context;
    }

    /// <summary>
    /// GH-5678. Called by <see cref="TransactionalFrame" /> when this frame opened the session that a transactional
    /// chain saves, typically because middleware asked for it before the transactional frame did. The session is then
    /// enlisted in the outbox right where it is opened, before any middleware or the handler uses it
    /// </summary>
    internal void EnlistInOutbox(bool useOptimisticConcurrency)
    {
        _enlistInOutbox = true;
        _useOptimisticConcurrency = useOptimisticConcurrency;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (!_enlistInOutbox || _context == null)
        {
            base.GenerateCode(method, writer);
            return;
        }

        var session = ReturnVariable!;

        writer.BlankLine();
        writer.WriteComment("Open a new document session, enlisted in the");
        writer.WriteComment("message context to support the outbox functionality");
        writer.Write($"using var {session.Usage} = {Target!.Usage}.{nameof(IDocumentStore.OpenAsyncSession)}();");
        writer.Write(
            $"{_context.Usage}.{nameof(MessageContext.EnlistInOutbox)}(new {typeof(RavenDbEnvelopeTransaction).FullName}({session.Usage}, {_context.Usage}));");

        if (_useOptimisticConcurrency)
        {
            writer.WriteComment("Use optimistic concurrency for sagas");
            writer.Write($"{session.Usage}.Advanced.UseOptimisticConcurrency = true;");
        }

        Next?.GenerateCode(method, writer);
    }
}
