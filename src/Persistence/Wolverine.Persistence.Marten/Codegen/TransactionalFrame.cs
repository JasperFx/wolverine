using System;
using System.Collections.Generic;
using Wolverine.Persistence.Marten.Publishing;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Marten;

namespace Wolverine.Persistence.Marten.Codegen;

public class TransactionalFrame : Frame
{
    private readonly IList<Loaded> _loadedDocs = new List<Loaded>();

    private readonly IList<Variable> _saved = new List<Variable>();
    private Variable? _context;
    private bool _createsSession;
    private Variable? _factory;

    public TransactionalFrame() : base(true)
    {
    }

    public Variable? Session { get; private set; }

    public Variable LoadDocument(Type documentType, Variable docId)
    {
        var document = new Variable(documentType, this);
        var loaded = new Loaded(document, documentType, docId);
        _loadedDocs.Add(loaded);

        return document;
    }

    public void SaveDocument(Variable document)
    {
        _saved.Add(document);
    }


    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Session = chain.TryFindVariable(typeof(IDocumentSession), VariableSource.NotServices);
        if (Session == null)
        {
            _createsSession = true;
            Session = new Variable(typeof(IDocumentSession), this);

            _factory = chain.FindVariable(typeof(OutboxedSessionFactory));
            yield return _factory;
        }

        // Inside of messaging. Not sure how this is gonna work for HTTP yet
        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);

        if (_context != null)
        {
            yield return _context;
        }

        if (Session != null)
        {
            yield return Session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_createsSession)
        {
            writer.BlankLine();
            writer.WriteComment("Open a new document session");
            writer.Write(
                $"using var {Session!.Usage} = {_factory!.Usage}.{nameof(OutboxedSessionFactory.OpenSession)}({_context!.Usage});");
        }

        foreach (var loaded in _loadedDocs) loaded.Write(writer, Session!);

        Next?.GenerateCode(method, writer);


        foreach (var saved in _saved)
        {
            writer.Write($"{Session!.Usage}.{nameof(IDocumentSession.Store)}({saved.Usage});");
        }

        writer.BlankLine();
        writer.WriteComment("Commit the unit of work");
        writer.Write(
            $"await {Session!.Usage}.{nameof(IDocumentSession.SaveChangesAsync)}(cancellation).ConfigureAwait(false);");
    }

    public class Loaded
    {
        private readonly Variable _docId;
        private readonly Variable _document;
        private readonly Type _documentType;

        public Loaded(Variable document, Type documentType, Variable docId)
        {
            _documentType = documentType ?? throw new ArgumentNullException(nameof(documentType));

            _document = document ?? throw new ArgumentNullException(nameof(document));

            _docId = docId ?? throw new ArgumentNullException(nameof(docId));
        }

        public void Write(ISourceWriter writer, Variable session)
        {
            writer.Write(
                $"var {_document.Usage} = await {session.Usage}.{nameof(IDocumentSession.LoadAsync)}<{_documentType.FullNameInCode()}>({_docId.Usage});");
        }
    }
}
