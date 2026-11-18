using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Wolverine.SourceGeneration;

internal enum SuspendKind
{
    Invoke,
    WaitFor,
    Delay
}

/// <summary>One await that ends a saga step</summary>
internal sealed class SuspendPoint
{
    public SuspendPoint(SuspendKind kind, StatementSyntax statement, InvocationExpressionSyntax invocation,
        IMethodSymbol member)
    {
        Kind = kind;
        Statement = statement;
        Invocation = invocation;
        Member = member;
    }

    public SuspendKind Kind { get; }
    public StatementSyntax Statement { get; }
    public InvocationExpressionSyntax Invocation { get; }
    public IMethodSymbol Member { get; }

    /// <summary>1-based, in source order. Step 0 is the start.</summary>
    public int Step { get; set; }

    /// <summary>The message type that resumes it (InvokeAsync and WaitForAsync)</summary>
    public ITypeSymbol? MessageType => Kind == SuspendKind.Delay ? null : Member.TypeArguments[0];

    /// <summary>var x = await ctx...;</summary>
    public ILocalSymbol? DeclaredLocal { get; set; }

    /// <summary>x = await ctx...;</summary>
    public ExpressionSyntax? AssignedTo { get; set; }

    public bool ResumesOnTimeout => Kind != SuspendKind.Invoke;
}

/// <summary>A local (or the triggering message) that lives across a step, so it is a property of the saga</summary>
internal sealed class HoistedValue
{
    public HoistedValue(ISymbol symbol, string propertyName, ITypeSymbol type)
    {
        Symbol = symbol;
        PropertyName = propertyName;
        Type = type;
    }

    public ISymbol Symbol { get; }
    public string PropertyName { get; }
    public ITypeSymbol Type { get; }
}

/// <summary>
/// GH-5660 SPIKE. Reads a [DurableWorkflow] method: where it waits, which locals live across a wait, and everything
/// the first cut refuses.
/// </summary>
internal sealed class WorkflowAnalysis
{
    public const string ContextInterface = "Wolverine.Workflows.IWorkflowContext";
    private const string SagaIdentityAttribute = "Wolverine.Persistence.Sagas.SagaIdentityAttribute";

    private static readonly HashSet<string> SuspendingMembers = ["InvokeAsync", "WaitForAsync", "DelayAsync"];

    // Members of the generated saga and its base class that a hoisted local must not collide with
    private static readonly HashSet<string> ReservedNames =
    [
        "Id", "Step", "WaitingFor", "Undo", "Status", "CompensationLog", "Version", "LastModified", "CompletedAt",
        "Start", "Handle", "NotFound", "Configure", "WorkflowName", "WorkflowVersion", "IsCompleted"
    ];

    private readonly List<DiagnosticInfo> _diagnostics = new();
    private readonly HashSet<InvocationExpressionSyntax> _awaitedSuspends = new();

    public WorkflowAnalysis(MethodDeclarationSyntax syntax, IMethodSymbol method, SemanticModel model)
    {
        Syntax = syntax;
        Method = method;
        Model = model;
    }

    public MethodDeclarationSyntax Syntax { get; }
    public IMethodSymbol Method { get; }
    public SemanticModel Model { get; }
    public BlockSyntax Body => Syntax.Body!;

    public IParameterSymbol Trigger { get; private set; } = null!;
    public IParameterSymbol Context { get; private set; } = null!;
    public List<IParameterSymbol> Services { get; } = new();

    public List<SuspendPoint> Suspends { get; } = new();
    public Dictionary<ISymbol, HoistedValue> Hoisted { get; } = new(SymbolEqualityComparer.Default);
    public List<HoistedValue> HoistedInOrder { get; } = new();

    /// <summary>Unqualified references to static members of the workflow class, which the saga has to qualify</summary>
    public HashSet<SyntaxNode> NeedsQualifier { get; } = new();

    public IReadOnlyList<DiagnosticInfo> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Any(x => WorkflowDiagnostics.Find(x.Id)!.DefaultSeverity == DiagnosticSeverity.Error);

    public string? Namespace { get; private set; }
    public string WorkflowClass => Method.ContainingType.Name;
    public string WorkflowName { get; private set; } = "";
    public int WorkflowVersion { get; private set; } = 1;
    public string SagaName { get; private set; } = "";
    public string? IdMember { get; private set; }

    /// <summary>A position at namespace scope in the workflow's file, for writing type names the way it would</summary>
    public int TypePosition => Syntax.Parent!.SpanStart;

    public void Analyze(CancellationToken token)
    {
        if (!checkShape()) return;

        nameTheWorkflow();
        findSuspends();
        if (Suspends.Count > 0)
        {
            checkAroundEachSuspend();
            refuseMethodWideConstructs();
        }

        token.ThrowIfCancellationRequested();

        hoistLocals();
        checkReferencesToTheWorkflowClass();
        findTheWorkflowId();
        checkCorrelation();
    }

    public string TypeName(ITypeSymbol type)
    {
        var format = SymbolDisplayFormat.MinimallyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        return type.ToMinimalDisplayString(Model, TypePosition, format);
    }

    public bool ContainsSuspend(SyntaxNode node)
    {
        return Suspends.Any(x => node.Span.Contains(x.Statement.Span));
    }

    public IMethodSymbol? ContextMember(InvocationExpressionSyntax invocation)
    {
        var symbol = Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol == null) return null;

        var type = symbol.ContainingType;
        return isContext(type) ? symbol : null;
    }

    private static bool isContext(ITypeSymbol? type)
    {
        if (type == null) return false;
        return type.ToDisplayString() == ContextInterface ||
               type.AllInterfaces.Any(x => x.ToDisplayString() == ContextInterface);
    }

    private void report(DiagnosticDescriptor descriptor, SyntaxNode node, params string[] args)
    {
        _diagnostics.Add(new DiagnosticInfo(descriptor, node.GetLocation(), args));
    }

    private void report(DiagnosticDescriptor descriptor, Location location, params string[] args)
    {
        _diagnostics.Add(new DiagnosticInfo(descriptor, location, args));
    }

    private string describe(SyntaxNode node)
    {
        var text = node.WithoutTrivia().ToString();
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text.Substring(0, newline).TrimEnd() + " ...";
    }

    // Nodes of the workflow method itself, not of the lambdas and local functions inside it
    private IEnumerable<SyntaxNode> workflowNodes()
    {
        return Body.DescendantNodes(x => x is not AnonymousFunctionExpressionSyntax && x is not LocalFunctionStatementSyntax);
    }

    private bool checkShape()
    {
        var name = $"{Method.ContainingType.Name}.{Method.Name}";
        var location = Syntax.Identifier.GetLocation();
        var ok = true;

        if (!Method.IsStatic)
        {
            report(WorkflowDiagnostics.NotStatic, location, name);
            ok = false;
        }

        if (Syntax.Body == null)
        {
            report(WorkflowDiagnostics.BadShape, location, name, "it needs a block body");
            return false;
        }

        if (Method.ReturnType.ToDisplayString() != "System.Threading.Tasks.Task")
        {
            report(WorkflowDiagnostics.BadShape, location, name, "it must return Task");
            ok = false;
        }

        if (Method.IsGenericMethod)
        {
            report(WorkflowDiagnostics.BadShape, location, name, "it can't be generic");
            ok = false;
        }

        if (Method.ContainingType.ContainingType != null || Method.ContainingType.IsGenericType)
        {
            report(WorkflowDiagnostics.BadShape, location, name, "its class must be a top-level, non-generic class");
            ok = false;
        }

        var contexts = Method.Parameters.Where(x => isContext(x.Type)).ToArray();
        if (contexts.Length != 1)
        {
            report(WorkflowDiagnostics.BadShape, location, name, "it must take exactly one IWorkflowContext parameter");
            return false;
        }

        if (Method.Parameters.Length < 2 || isContext(Method.Parameters[0].Type))
        {
            report(WorkflowDiagnostics.BadShape, location, name,
                "its first parameter must be the triggering message, then IWorkflowContext, then any services");
            return false;
        }

        if (Method.Parameters.Any(x => x.RefKind != RefKind.None))
        {
            report(WorkflowDiagnostics.BadShape, location, name, "its parameters can't be ref, out or in");
            ok = false;
        }

        Context = contexts[0];
        Trigger = Method.Parameters[0];
        Services.AddRange(Method.Parameters.Skip(1).Where(x => !SymbolEqualityComparer.Default.Equals(x, Context)));

        return ok;
    }

    private void nameTheWorkflow()
    {
        var ns = Method.ContainingType.ContainingNamespace;
        Namespace = ns.IsGlobalNamespace ? null : ns.ToDisplayString();

        WorkflowName = $"{Method.ContainingType.Name}.{Method.Name}";
        var attribute = Method.GetAttributes()
            .FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == DurableWorkflowGenerator.AttributeName);
        if (attribute != null)
        {
            if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string named)
            {
                WorkflowName = named;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.Key == "Name" && argument.Value.Value is string n) WorkflowName = n;
                if (argument.Key == "Version" && argument.Value.Value is int v) WorkflowVersion = v;
            }
        }

        // PlaceOrderWorkflow.Run => PlaceOrderWorkflowSaga, OrderWorkflows.Ship => OrderWorkflowsShipSaga. One saga per
        // version, side by side (#5405 Q5)
        var method = Method.Name is "Run" or "RunAsync" ? "" : Method.Name;
        var version = WorkflowVersion > 1 ? $"V{WorkflowVersion}" : "";
        SagaName = $"{Method.ContainingType.Name}{method}{version}Saga";
    }

    public string HintName => $"{(Namespace == null ? "" : Namespace + ".")}{SagaName}.g.cs";

    private void findSuspends()
    {
        foreach (var node in workflowNodes())
        {
            switch (node)
            {
                case AwaitExpressionSyntax awaitExpression:
                    classifyAwait(awaitExpression);
                    break;

                case CommonForEachStatementSyntax { AwaitKeyword.RawKind: not 0 } forEach:
                    report(WorkflowDiagnostics.AwaitOnSomethingElse, forEach.AwaitKeyword.GetLocation(), "await foreach");
                    break;

                case UsingStatementSyntax { AwaitKeyword.RawKind: not 0 } usingStatement:
                    report(WorkflowDiagnostics.AwaitOnSomethingElse, usingStatement.AwaitKeyword.GetLocation(), "await using");
                    break;

                case LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } declaration:
                    report(WorkflowDiagnostics.AwaitOnSomethingElse, declaration.AwaitKeyword.GetLocation(), "await using");
                    break;
            }
        }

        // A waiting member that isn't awaited as a statement of the method itself: stored in a variable, awaited later,
        // passed to Task.WhenAll, or called inside a lambda
        foreach (var invocation in Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (_awaitedSuspends.Contains(invocation)) continue;

            var member = ContextMember(invocation);
            if (member != null && SuspendingMembers.Contains(member.Name) &&
                !Suspends.Any(x => x.Invocation == invocation))
            {
                if (!_diagnostics.Any(x => x.Span.Contains(invocation.Span) && x.Id == WorkflowDiagnostics.WaitNotAStatement.Id))
                {
                    report(WorkflowDiagnostics.WaitNotAStatement, invocation, describe(invocation));
                }
            }
        }

        var step = 1;
        foreach (var suspend in Suspends.OrderBy(x => x.Statement.SpanStart))
        {
            suspend.Step = step++;
        }

        Suspends.Sort((a, b) => a.Step.CompareTo(b.Step));
    }

    private void classifyAwait(AwaitExpressionSyntax awaitExpression)
    {
        var operand = awaitExpression.Expression;
        while (operand is ParenthesizedExpressionSyntax parenthesized) operand = parenthesized.Expression;

        var member = operand is InvocationExpressionSyntax invocation ? ContextMember(invocation) : null;
        if (member == null)
        {
            report(WorkflowDiagnostics.AwaitOnSomethingElse, awaitExpression, describe(awaitExpression));
            return;
        }

        if (!SuspendingMembers.Contains(member.Name)) return; // SendAsync, PublishAsync, RunAsync: awaited in place

        var call = (InvocationExpressionSyntax)operand;
        _awaitedSuspends.Add(call);

        var kind = member.Name switch
        {
            "InvokeAsync" => SuspendKind.Invoke,
            "WaitForAsync" => SuspendKind.WaitFor,
            _ => SuspendKind.Delay
        };

        var parent = awaitExpression.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;

        switch (parent)
        {
            case ExpressionStatementSyntax statement:
                Suspends.Add(new SuspendPoint(kind, statement, call, member));
                return;

            case EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax
                {
                    Parent: VariableDeclarationSyntax
                    {
                        Variables.Count: 1,
                        Parent: LocalDeclarationStatementSyntax declaration
                    }
                } declarator
            } when declaration.UsingKeyword.RawKind == 0 && !declaration.IsConst && kind != SuspendKind.Delay:
                Suspends.Add(new SuspendPoint(kind, declaration, call, member)
                {
                    DeclaredLocal = Model.GetDeclaredSymbol(declarator) as ILocalSymbol
                });
                return;

            case AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Parent: ExpressionStatementSyntax assignmentStatement
            } assignment when kind != SuspendKind.Delay && Model.GetSymbolInfo(assignment.Left).Symbol is ILocalSymbol:
                Suspends.Add(new SuspendPoint(kind, assignmentStatement, call, member)
                {
                    AssignedTo = assignment.Left
                });
                return;
        }

        report(WorkflowDiagnostics.WaitNotAStatement, awaitExpression, describe(awaitExpression));
    }

    private void checkAroundEachSuspend()
    {
        foreach (var suspend in Suspends)
        {
            var wait = describe(suspend.Invocation.Parent!);
            SyntaxNode previous = suspend.Statement;
            for (var node = suspend.Statement.Parent; node != null && node != Body; previous = node, node = node.Parent)
            {
                switch (node)
                {
                    case BlockSyntax:
                    case IfStatementSyntax:
                    case ElseClauseSyntax:
                    case CatchClauseSyntax:
                    case FinallyClauseSyntax:
                        break;

                    case ForStatementSyntax:
                        report(WorkflowDiagnostics.LoopAcrossAwait, suspend.Invocation.Parent!, wait, "for");
                        break;
                    case CommonForEachStatementSyntax:
                        report(WorkflowDiagnostics.LoopAcrossAwait, suspend.Invocation.Parent!, wait, "foreach");
                        break;
                    case WhileStatementSyntax:
                        report(WorkflowDiagnostics.LoopAcrossAwait, suspend.Invocation.Parent!, wait, "while");
                        break;
                    case DoStatementSyntax:
                        report(WorkflowDiagnostics.LoopAcrossAwait, suspend.Invocation.Parent!, wait, "do");
                        break;

                    case TryStatementSyntax tryStatement:
                        var part = previous switch
                        {
                            CatchClauseSyntax => "catch block",
                            FinallyClauseSyntax => "finally block",
                            _ => tryStatement.Finally != null ? "try block with a finally" : "try block"
                        };
                        report(WorkflowDiagnostics.BlockAcrossAwait, suspend.Invocation.Parent!, wait, part);
                        break;

                    case UsingStatementSyntax:
                        report(WorkflowDiagnostics.BlockAcrossAwait, suspend.Invocation.Parent!, wait, "using block");
                        break;

                    case LockStatementSyntax:
                        report(WorkflowDiagnostics.BlockAcrossAwait, suspend.Invocation.Parent!, wait, "lock block");
                        break;

                    case SwitchSectionSyntax:
                    case SwitchStatementSyntax:
                        if (node is SwitchStatementSyntax)
                        {
                            report(WorkflowDiagnostics.UnsupportedConstruct, suspend.Invocation.Parent!, "switch statement");
                        }

                        break;

                    case LabeledStatementSyntax:
                        report(WorkflowDiagnostics.UnsupportedConstruct, suspend.Invocation.Parent!, "labeled statement");
                        break;

                    default:
                        report(WorkflowDiagnostics.UnsupportedConstruct, suspend.Invocation.Parent!,
                            node.Kind().ToString());
                        break;
                }

                // using var x = ...; earlier in an enclosing block would have to stay undisposed until the next message
                if (node is BlockSyntax block && previous is StatementSyntax inBlock)
                {
                    foreach (var earlier in block.Statements.TakeWhile(x => x != inBlock))
                    {
                        if (earlier is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
                        {
                            report(WorkflowDiagnostics.BlockAcrossAwait, suspend.Invocation.Parent!, wait,
                                "using declaration");
                        }
                    }
                }
            }

            // The same check for the method body itself
            if (findStatementInBody(suspend.Statement) is { } top)
            {
                foreach (var earlier in Body.Statements.TakeWhile(x => x != top))
                {
                    if (earlier is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
                    {
                        report(WorkflowDiagnostics.BlockAcrossAwait, suspend.Invocation.Parent!, wait, "using declaration");
                    }
                }
            }
        }
    }

    private StatementSyntax? findStatementInBody(SyntaxNode node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current.Parent == Body) return current as StatementSyntax;
        }

        return null;
    }

    private void refuseMethodWideConstructs()
    {
        foreach (var node in workflowNodes())
        {
            switch (node)
            {
                case GotoStatementSyntax:
                    report(WorkflowDiagnostics.UnsupportedConstruct, node, "goto");
                    break;
                case LabeledStatementSyntax labeled when !ContainsSuspend(labeled):
                    report(WorkflowDiagnostics.UnsupportedConstruct, labeled.Identifier.GetLocation(), "label");
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    report(WorkflowDiagnostics.UnsupportedConstruct, localFunction.Identifier.GetLocation(),
                        "local function");
                    break;
            }
        }
    }

    private void hoistLocals()
    {
        // The triggering message is always kept: the saga needs it for its id, and CritterWatch can show it
        addHoisted(Trigger, Trigger.Type);

        var declared = new List<(ILocalSymbol local, int declaredAt, bool plain, SyntaxNode node)>();
        foreach (var node in workflowNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    if (Model.GetDeclaredSymbol(declarator) is ILocalSymbol local)
                    {
                        var plain = declarator.Parent is VariableDeclarationSyntax
                        {
                            Variables.Count: 1,
                            Parent: LocalDeclarationStatementSyntax { UsingKeyword.RawKind: 0, IsConst: false }
                        };
                        declared.Add((local, declarator.Span.End, plain, declarator));
                    }

                    break;

                case SingleVariableDesignationSyntax designation:
                    if (Model.GetDeclaredSymbol(designation) is ILocalSymbol designated)
                    {
                        declared.Add((designated, designation.Span.End, false, designation));
                    }

                    break;

                case ForEachStatementSyntax forEach:
                    if (Model.GetDeclaredSymbol(forEach) is ILocalSymbol loopVariable)
                    {
                        declared.Add((loopVariable, forEach.SpanStart, false, forEach));
                    }

                    break;

                case CatchDeclarationSyntax catchDeclaration:
                    if (Model.GetDeclaredSymbol(catchDeclaration) is ILocalSymbol caught)
                    {
                        declared.Add((caught, catchDeclaration.Span.End, false, catchDeclaration));
                    }

                    break;
            }
        }

        if (Suspends.Count == 0) return;

        var references = Body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(x => (node: x, symbol: Model.GetSymbolInfo(x).Symbol))
            .Where(x => x.symbol is ILocalSymbol)
            .ToLookup(x => x.symbol!, x => x.node, SymbolEqualityComparer.Default);

        foreach (var (local, declaredAt, plain, node) in declared)
        {
            // Used after a wait that comes after its declaration. There are no loops around a wait, so source order is
            // the order things run in
            var livesAcross = references[local].Any(reference => Suspends.Any(s =>
                s.Statement.SpanStart >= declaredAt && s.Statement.Span.End <= reference.SpanStart));

            // x = await ctx...; assigns x in the next step, so x has to be on the saga
            var assignedByAWait = Suspends.Any(s =>
                s.AssignedTo != null &&
                SymbolEqualityComparer.Default.Equals(Model.GetSymbolInfo(s.AssignedTo).Symbol, local));

            if (!livesAcross && !assignedByAWait) continue;

            if (!plain)
            {
                report(WorkflowDiagnostics.LocalNotHoistable, node, local.Name);
                continue;
            }

            var problem = whyNotSerializable(local.Type, 0);
            if (problem != null)
            {
                report(WorkflowDiagnostics.NotSerializable, node, local.Name, TypeName(local.Type), problem);
                continue;
            }

            addHoisted(local, local.Type);
        }
    }

    private void addHoisted(ISymbol symbol, ITypeSymbol type)
    {
        var name = symbol.Name.TrimStart('@', '_');
        if (name.Length == 0) name = "Value";
        name = char.ToUpperInvariant(name[0]) + name.Substring(1);

        var candidate = name;
        var counter = 2;
        while (ReservedNames.Contains(candidate) || candidate == SagaName ||
               HoistedInOrder.Any(x => x.PropertyName == candidate))
        {
            candidate = name + counter++;
        }

        var hoisted = new HoistedValue(symbol, candidate, type);
        Hoisted[symbol] = hoisted;
        HoistedInOrder.Add(hoisted);
    }

    /// <summary>Best effort: what keeps a type from going into a saga document as JSON and coming back</summary>
    private string? whyNotSerializable(ITypeSymbol type, int depth)
    {
        if (depth > 4) return null;

        switch (type)
        {
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                return "a pointer";
            case IDynamicTypeSymbol:
                return "dynamic has no type to come back as";
            case IArrayTypeSymbol array:
                return whyNotSerializable(array.ElementType, depth + 1);
            case ITypeParameterSymbol:
                return null;
        }

        if (type.SpecialType == SpecialType.System_Object) return "object has no type to come back as";
        if (type.IsRefLikeType) return "a ref struct";
        if (type.TypeKind == TypeKind.Delegate) return "a delegate";

        var display = type.OriginalDefinition.ToDisplayString();
        if (display is "System.Threading.CancellationToken" or "System.Type" ||
            display.StartsWith("System.Reflection.", StringComparison.Ordinal))
        {
            return "it only means something inside one process";
        }

        if (type.GetMembers("GetAwaiter").Any() || display.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal))
        {
            return "a task can't outlive the step that started it";
        }

        if (type.AllInterfaces.Any(x => x.ToDisplayString() is "System.IDisposable" or "System.IAsyncDisposable"))
        {
            return "it is disposable, so it holds something that can't be stored";
        }

        if (type is INamedTypeSymbol named)
        {
            if (named.TypeKind == TypeKind.Interface || named.IsAbstract)
            {
                var ns = named.ContainingNamespace?.ToDisplayString();
                if (ns != "System.Collections.Generic")
                {
                    return "an interface or abstract type has no concrete type to come back as";
                }
            }

            foreach (var argument in named.TypeArguments)
            {
                var problem = whyNotSerializable(argument, depth + 1);
                if (problem != null) return problem;
            }
        }

        return null;
    }

    private void checkReferencesToTheWorkflowClass()
    {
        var workflowType = Method.ContainingType;

        foreach (var name in Body.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var info = Model.GetSymbolInfo(name);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol == null || symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ILabelSymbol)
            {
                continue;
            }

            if (!declaredIn(symbol, workflowType)) continue;

            if (symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                or Accessibility.ProtectedAndInternal)
            {
                report(WorkflowDiagnostics.PrivateMember, name, name.Identifier.Text, workflowType.Name);
                continue;
            }

            var qualified = name.Parent switch
            {
                MemberAccessExpressionSyntax access => access.Name == name,
                QualifiedNameSyntax qualifiedName => qualifiedName.Right == name,
                MemberBindingExpressionSyntax => true,
                NameColonSyntax => true,
                _ => false
            };

            if (!qualified && (symbol.IsStatic || symbol is INamedTypeSymbol))
            {
                NeedsQualifier.Add(name);
            }
        }
    }

    private static bool declaredIn(ISymbol symbol, INamedTypeSymbol workflowType)
    {
        for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
        {
            for (var current = workflowType; current != null && current.SpecialType != SpecialType.System_Object;
                 current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(type, current)) return true;
            }
        }

        return false;
    }

    private void findTheWorkflowId()
    {
        // The same order Wolverine's SagaChain uses for a message's saga id
        var members = membersOf(Trigger.Type).ToArray();
        var expected = SagaName + "Id";
        var member = members.FirstOrDefault(x => x.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == SagaIdentityAttribute))
                     ?? members.FirstOrDefault(x => x.Name == expected)
                     ?? members.FirstOrDefault(x => x.Name == expected.Replace("Saga", ""))
                     ?? members.FirstOrDefault(x => x.Name == "SagaId")
                     ?? members.FirstOrDefault(x => string.Equals(x.Name, "Id", StringComparison.OrdinalIgnoreCase));

        if (member == null) return; // the saga-id header, or a new id

        var memberType = member is IPropertySymbol p ? p.Type : ((IFieldSymbol)member).Type;
        if (memberType.ToDisplayString() != "System.Guid")
        {
            report(WorkflowDiagnostics.BadShape, Syntax.Identifier.GetLocation(), WorkflowName,
                $"the spike only supports Guid workflow ids, and {Trigger.Type.Name}.{member.Name} is {TypeName(memberType)}");
            return;
        }

        IdMember = member.Name;
    }

    private static IEnumerable<ISymbol> membersOf(ITypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
                if (member is IPropertySymbol { IsIndexer: false } or IFieldSymbol { IsImplicitlyDeclared: false })
                {
                    yield return member;
                }
            }
        }
    }

    private void checkCorrelation()
    {
        foreach (var suspend in Suspends)
        {
            if (suspend.Kind == SuspendKind.Invoke) continue; // the reply comes back through the saga-id header

            if (suspend.MessageType is { } type)
            {
                if (SymbolEqualityComparer.Default.Equals(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
                        Trigger.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)))
                {
                    report(WorkflowDiagnostics.BadShape, suspend.Invocation, WorkflowName,
                        "it can't wait for its own triggering message type");
                    continue;
                }

                var names = membersOf(type).ToArray();
                var correlated = names.Any(x =>
                    x.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == SagaIdentityAttribute) ||
                    x.Name == SagaName + "Id" || x.Name == SagaName.Replace("Saga", "") + "Id" || x.Name == "SagaId" ||
                    string.Equals(x.Name, "Id", StringComparison.OrdinalIgnoreCase));

                if (!correlated)
                {
                    report(WorkflowDiagnostics.UncorrelatedWait, suspend.Invocation, type.Name);
                }
            }
        }
    }

    /// <summary>The arguments of a waiting call, by the context member's parameter names, rewritten for the saga</summary>
    public Dictionary<string, ExpressionSyntax> ArgumentsOf(SuspendPoint suspend)
    {
        var arguments = new Dictionary<string, ExpressionSyntax>();
        if (Model.GetOperation(suspend.Invocation) is IInvocationOperation operation)
        {
            foreach (var argument in operation.Arguments)
            {
                if (argument.ArgumentKind == ArgumentKind.DefaultValue) continue;
                if (argument.Syntax is ArgumentSyntax syntax && argument.Parameter != null)
                {
                    arguments[argument.Parameter.Name] = syntax.Expression;
                }
            }
        }

        return arguments;
    }
}
