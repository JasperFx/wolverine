using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Wolverine.SourceGeneration;

/// <summary>
/// GH-5660 SPIKE. Copies the workflow's own statements into the saga, changing only what has to change:
/// <list type="bullet">
/// <item>a local that lives across a step (and the triggering message) becomes the saga property that holds it</item>
/// <item>its declaration becomes an assignment to that property</item>
/// <item>`return;` completes the workflow before it returns</item>
/// <item>an unqualified static member of the workflow class gets the class name in front of it</item>
/// </list>
/// Always called with nodes of the original tree, so the semantic model can answer for them.
/// </summary>
internal sealed class WorkflowRewriter : CSharpSyntaxRewriter
{
    private static readonly SyntaxAnnotation FlattenMe = new("wolverine-workflow-flatten");

    /// <summary>A rewritten `return;` comes back as two statements in a block that belongs to its parent</summary>
    public static IEnumerable<StatementSyntax> Flatten(StatementSyntax statement)
    {
        return statement is BlockSyntax block && block.HasAnnotation(FlattenMe)
            ? (IEnumerable<StatementSyntax>)block.Statements
            : new[] { statement };
    }

    private readonly WorkflowAnalysis _analysis;
    private readonly string _workflowClass;

    public WorkflowRewriter(WorkflowAnalysis analysis)
    {
        _analysis = analysis;
        _workflowClass = analysis.TypeName(analysis.Method.ContainingType);
    }

    public StatementSyntax Rewrite(StatementSyntax statement) => (StatementSyntax)Visit(statement);

    public ExpressionSyntax Rewrite(ExpressionSyntax expression) => (ExpressionSyntax)Visit(expression);

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var symbol = _analysis.Model.GetSymbolInfo(node).Symbol;
        if (symbol != null && _analysis.Hoisted.TryGetValue(symbol, out var hoisted))
        {
            return IdentifierName(hoisted.PropertyName).WithTriviaFrom(node);
        }

        if (_analysis.NeedsQualifier.Contains(node))
        {
            return QualifiedName(ParseName(_workflowClass), node.WithoutTrivia()).WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
        if (_analysis.NeedsQualifier.Contains(node))
        {
            return QualifiedName(ParseName(_workflowClass), visited.WithoutTrivia()).WithTriviaFrom(node);
        }

        return visited;
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (node.Declaration.Variables.Count == 1 &&
            _analysis.Model.GetDeclaredSymbol(node.Declaration.Variables[0]) is { } local &&
            _analysis.Hoisted.TryGetValue(local, out var hoisted))
        {
            var initializer = node.Declaration.Variables[0].Initializer;
            var value = initializer == null
                ? LiteralExpression(SyntaxKind.DefaultLiteralExpression)
                : (ExpressionSyntax)Visit(initializer.Value);

            return ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(hoisted.PropertyName), value))
                .WithTriviaFrom(node);
        }

        return base.VisitLocalDeclarationStatement(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (insideANestedFunction(node)) return base.VisitReturnStatement(node);

        // The workflow method returned: the workflow is complete
        return Block(ParseStatement("CompleteWorkflow();"), ReturnStatement())
            .WithAdditionalAnnotations(FlattenMe)
            .WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        if (!visited.Statements.Any(x => x.HasAnnotation(FlattenMe))) return visited;

        var statements = visited.Statements.SelectMany(x =>
            x is BlockSyntax inner && inner.HasAnnotation(FlattenMe) ? (IEnumerable<StatementSyntax>)inner.Statements : new[] { x });

        return visited.WithStatements(List(statements));
    }

    private bool insideANestedFunction(SyntaxNode node)
    {
        for (var current = node.Parent; current != null && current != _analysis.Body; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) return true;
        }

        return false;
    }
}
