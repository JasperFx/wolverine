using System;
using System.Collections.Generic;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Codegen;

public class TransactionalFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly IList<Loaded> _loadedDocs = new List<Loaded>();

    private readonly IList<Variable> _saved = new List<Variable>();
    private Variable? _messageContext;

    public TransactionalFrame(Type dbContextType)
    {
        _dbContextType = dbContextType;
    }

    private Variable? _dbContext;

    public Variable LoadDocument(Type documentType, Variable docId)
    {
        var document = new Variable(documentType, this);
        var loaded = new Loaded(document, documentType, docId);
        _loadedDocs.Add(loaded);

        return document;
    }

    public void InsertEntity(Variable document)
    {
        _saved.Add(document);
    }


    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _dbContext = chain.FindVariable(_dbContextType);

        // Inside of messaging. Not sure how this is gonna work for HTTP yet
        _messageContext = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices) ??
                   chain.FindVariable(typeof(IMessageContext));

        if (_messageContext != null)
        {
            yield return _messageContext;
        }

        if (_dbContext != null)
        {
            yield return _dbContext;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {typeof(WolverineEnvelopeEntityFrameworkCoreExtensions).FullName}.{nameof(WolverineEnvelopeEntityFrameworkCoreExtensions.EnlistInOutboxAsync)}({_messageContext!.Usage}, {_dbContext!.Usage});");

        foreach (var loaded in _loadedDocs) loaded.Write(writer, _dbContext);

        Next?.GenerateCode(method, writer);


        foreach (var saved in _saved)
            writer.Write($"{_dbContext.Usage}.{nameof(DbContext.Add)}({saved.Usage});");

        writer.BlankLine();
        writer.WriteComment("Commit the unit of work");
        writer.Write(
            $"await {_dbContext.Usage}.{nameof(DbContext.SaveChangesAsync)}(cancellation).ConfigureAwait(false);");
    }

    private class Loaded
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
                $"var {_document.Usage} = await {session.Usage}.{nameof(DbContext.FindAsync)}<{_documentType.FullNameInCode()}>({_docId.Usage});");
        }
    }
}
