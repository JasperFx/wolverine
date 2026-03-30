using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Wolverine.SourceGeneration
{
    /// <summary>
    /// Roslyn incremental source generator that discovers Wolverine handler types,
    /// message types, pre-generated handler code, and extension types at compile time,
    /// emitting an IWolverineTypeLoader implementation that eliminates runtime assembly
    /// scanning during startup.
    /// </summary>
    [Generator]
    public class WolverineTypeManifestGenerator : IIncrementalGenerator
    {
        // Handler type name suffixes matching Wolverine conventions
        private const string HandlerSuffix = "Handler";
        private const string ConsumerSuffix = "Consumer";

        // Well-known Wolverine attribute and interface full names
        private const string WolverineHandlerAttributeFullName = "Wolverine.Attributes.WolverineHandlerAttribute";
        private const string WolverineIgnoreAttributeFullName = "Wolverine.Attributes.WolverineIgnoreAttribute";
        private const string WolverineMessageAttributeFullName = "Wolverine.Attributes.WolverineMessageAttribute";
        private const string IWolverineHandlerFullName = "Wolverine.IWolverineHandler";
        private const string SagaFullName = "Wolverine.Saga";
        private const string IMessageFullName = "Wolverine.IMessage";

        // Phase D: Pre-generated handler types
        private const string MessageHandlerFullName = "Wolverine.Runtime.Handlers.MessageHandler";
        internal const string WolverineHandlersNamespaceConst = "WolverineHandlers";

        // Phase E: Extension discovery
        private const string IWolverineExtensionFullName = "Wolverine.IWolverineExtension";
        private const string WolverineModuleAttributeFullName = "Wolverine.Attributes.WolverineModuleAttribute";

        // Valid handler method names (matching HandlerChain and SagaChain constants)
        private static readonly HashSet<string> ValidMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Handle", "Handles", "HandleAsync", "HandlesAsync",
            "Consume", "Consumes", "ConsumeAsync", "ConsumesAsync",
            "Orchestrate", "Orchestrates", "OrchestrateAsync", "OrchestratesAsync",
            "Start", "Starts", "StartAsync", "StartsAsync",
            "StartOrHandle", "StartsOrHandles", "StartOrHandleAsync", "StartsOrHandlesAsync",
            "NotFound", "NotFoundAsync"
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Step 1: Find candidate class declarations that might be handlers or message types
            var classDeclarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidate(node),
                    transform: static (ctx, _) => ClassifyType(ctx))
                .Where(static result => result != null);

            // Step 2: Combine with compilation for final resolution
            var compilationAndClasses = context.CompilationProvider
                .Combine(classDeclarations.Collect());

            // Step 3: Emit the source
            context.RegisterSourceOutput(compilationAndClasses,
                static (spc, source) => Execute(source.Left, source.Right!, spc));
        }

        /// <summary>
        /// Fast syntactic predicate: is this node a class/record declaration that could
        /// potentially be a handler or message type?
        /// </summary>
        private static bool IsCandidate(SyntaxNode node)
        {
            // Accept class declarations and record declarations
            if (node is ClassDeclarationSyntax classDecl)
            {
                // Must be public (or nested public)
                if (!HasPublicModifier(classDecl.Modifiers))
                    return false;

                // Must not be abstract (unless it's checked later for Saga subclass)
                // We let everything through that's public; semantic analysis will refine
                return true;
            }

            if (node is RecordDeclarationSyntax recordDecl)
            {
                if (!HasPublicModifier(recordDecl.Modifiers))
                    return false;
                return true;
            }

            return false;
        }

        private static bool HasPublicModifier(SyntaxTokenList modifiers)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier.IsKind(SyntaxKind.PublicKeyword))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Semantic transform: classify a type as a handler, message type, or both.
        /// Returns null if the type doesn't match any Wolverine conventions.
        /// </summary>
        private static DiscoveredType? ClassifyType(GeneratorSyntaxContext context)
        {
            INamedTypeSymbol? classSymbol = null;

            if (context.Node is ClassDeclarationSyntax)
            {
                classSymbol = context.SemanticModel.GetDeclaredSymbol(context.Node) as INamedTypeSymbol;
            }
            else if (context.Node is RecordDeclarationSyntax)
            {
                classSymbol = context.SemanticModel.GetDeclaredSymbol(context.Node) as INamedTypeSymbol;
            }

            if (classSymbol == null) return null;
            if (classSymbol.IsAbstract) return null;
            if (classSymbol.IsGenericType && !classSymbol.IsDefinition) return null;
            // Skip open generic type definitions (e.g., Handler<T>) -- we only want concrete types
            if (classSymbol.IsGenericType) return null;
            if (classSymbol.DeclaredAccessibility != Accessibility.Public) return null;

            // Check for [WolverineIgnore]
            if (HasAttribute(classSymbol, WolverineIgnoreAttributeFullName))
                return null;

            var isHandler = IsHandlerType(classSymbol);
            var isMessage = IsMessageType(classSymbol);

            if (!isHandler && !isMessage) return null;

            var result = new DiscoveredType(
                classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                classSymbol.Name,
                classSymbol.ContainingNamespace?.ToDisplayString() ?? "",
                isHandler,
                isMessage);

            // If it's a handler, find message types from method parameters
            if (isHandler)
            {
                FindMessageTypesFromMethods(classSymbol, result);
            }

            return result;
        }

        /// <summary>
        /// Determines if a type qualifies as a Wolverine handler.
        /// Matches the same rules as HandlerDiscovery.specifyConventionalHandlerDiscovery().
        /// </summary>
        private static bool IsHandlerType(INamedTypeSymbol symbol)
        {
            // Rule 1: Name ends with "Handler" or "Consumer"
            if (symbol.Name.EndsWith(HandlerSuffix, StringComparison.Ordinal) ||
                symbol.Name.EndsWith(ConsumerSuffix, StringComparison.Ordinal))
            {
                return true;
            }

            // Rule 2: Decorated with [WolverineHandler]
            if (HasAttribute(symbol, WolverineHandlerAttributeFullName))
            {
                return true;
            }

            // Rule 3: Implements IWolverineHandler
            if (ImplementsInterface(symbol, IWolverineHandlerFullName))
            {
                return true;
            }

            // Rule 4: Inherits from Saga (directly or indirectly)
            if (InheritsFrom(symbol, SagaFullName))
            {
                return true;
            }

            // Rule 5: Has methods decorated with [WolverineHandler]
            foreach (var member in symbol.GetMembers())
            {
                if (member is IMethodSymbol method && HasAttribute(method, WolverineHandlerAttributeFullName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines if a type qualifies as a Wolverine message type.
        /// </summary>
        private static bool IsMessageType(INamedTypeSymbol symbol)
        {
            if (symbol.IsStatic) return false;
            if (!symbol.IsReferenceType && !symbol.IsValueType) return false;

            // Implements IMessage
            if (ImplementsInterface(symbol, IMessageFullName))
                return true;

            // Decorated with [WolverineMessage]
            if (HasAttribute(symbol, WolverineMessageAttributeFullName))
                return true;

            return false;
        }

        /// <summary>
        /// Find message types by inspecting the first parameter of handler methods.
        /// </summary>
        private static void FindMessageTypesFromMethods(INamedTypeSymbol handlerType, DiscoveredType result)
        {
            foreach (var member in handlerType.GetMembers())
            {
                if (!(member is IMethodSymbol method)) continue;
                if (method.DeclaredAccessibility != Accessibility.Public) continue;
                if (method.MethodKind != MethodKind.Ordinary) continue;
                if (method.Parameters.Length == 0) continue;

                // Check if method name matches handler conventions or has [WolverineHandler]
                var isHandlerMethod = ValidMethodNames.Contains(method.Name) ||
                                     HasAttribute(method, WolverineHandlerAttributeFullName);

                if (!isHandlerMethod) continue;

                // Check for [WolverineIgnore]
                if (HasAttribute(method, WolverineIgnoreAttributeFullName)) continue;

                // First parameter is the message type
                var firstParam = method.Parameters[0];
                var paramType = firstParam.Type;

                // Skip primitives, object, arrays of object, etc.
                if (paramType.SpecialType != SpecialType.None) continue;
                if (paramType.TypeKind == TypeKind.Interface) continue; // Skip interface params
                if (paramType.TypeKind == TypeKind.TypeParameter) continue; // Skip generic params

                // Skip open generic types (e.g., when handler method uses T as parameter type)
                if (paramType is INamedTypeSymbol namedParamType && namedParamType.IsGenericType)
                {
                    // Only allow closed generic types (all type arguments are concrete)
                    var hasUnboundTypeArgs = false;
                    foreach (var typeArg in namedParamType.TypeArguments)
                    {
                        if (typeArg.TypeKind == TypeKind.TypeParameter)
                        {
                            hasUnboundTypeArgs = true;
                            break;
                        }
                    }
                    if (hasUnboundTypeArgs) continue;
                }

                var fullTypeName = paramType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var alias = paramType.Name;

                result.MethodMessageTypes.Add((fullTypeName, alias));
            }
        }

        private static bool HasAttribute(ISymbol symbol, string attributeFullName)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                var attrClass = attr.AttributeClass;
                if (attrClass != null && attrClass.ToDisplayString() == attributeFullName)
                    return true;
            }
            return false;
        }

        private static bool ImplementsInterface(INamedTypeSymbol symbol, string interfaceFullName)
        {
            foreach (var iface in symbol.AllInterfaces)
            {
                if (iface.ToDisplayString() == interfaceFullName)
                    return true;
            }
            return false;
        }

        private static bool InheritsFrom(INamedTypeSymbol symbol, string baseClassFullName)
        {
            var current = symbol.BaseType;
            while (current != null)
            {
                if (current.ToDisplayString() == baseClassFullName)
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        /// <summary>
        /// Final emission step: generate the IWolverineTypeLoader implementation.
        /// </summary>
        private static void Execute(
            Compilation compilation,
            ImmutableArray<DiscoveredType?> discoveredTypes,
            SourceProductionContext context)
        {
            if (discoveredTypes.IsDefaultOrEmpty) return;

            // Check if the compilation references Wolverine (has IWolverineTypeLoader)
            var typeLoaderSymbol = compilation.GetTypeByMetadataName("Wolverine.Runtime.IWolverineTypeLoader");
            if (typeLoaderSymbol == null)
            {
                // This assembly doesn't reference Wolverine, skip generation
                return;
            }

            // Deduplicate and categorize
            var handlerTypes = new List<string>();
            var messageTypes = new Dictionary<string, string>(); // FullName -> Alias
            var handlerTypeNames = new HashSet<string>();

            foreach (var type in discoveredTypes)
            {
                if (type == null) continue;

                if (type.IsHandler && handlerTypeNames.Add(type.FullName))
                {
                    handlerTypes.Add(type.FullName);

                    // Add message types from handler method params
                    foreach (var (msgFullName, msgAlias) in type.MethodMessageTypes)
                    {
                        if (!messageTypes.ContainsKey(msgFullName))
                        {
                            messageTypes[msgFullName] = msgAlias;
                        }
                    }
                }

                if (type.IsMessage)
                {
                    var fullName = type.FullName;
                    if (!messageTypes.ContainsKey(fullName))
                    {
                        messageTypes[fullName] = type.ClassName;
                    }
                }
            }

            // Phase D: Find pre-generated handler types in the WolverineHandlers namespace
            // that inherit from MessageHandler. These are emitted by Wolverine's code generation
            // (dotnet run -- codegen) and can be looked up via dictionary instead of linear scan.
            var preGenHandlerTypes = FindPreGeneratedHandlerTypes(compilation);

            // Phase E: Find extension types implementing IWolverineExtension
            var extensionTypes = FindExtensionTypes(compilation);

            // Don't emit if we found nothing
            if (handlerTypes.Count == 0 && messageTypes.Count == 0
                && preGenHandlerTypes.Count == 0 && extensionTypes.Count == 0) return;

            var source = EmitTypeLoaderSource(handlerTypes, messageTypes, preGenHandlerTypes, extensionTypes);
            context.AddSource("WolverineTypeManifest.g.cs", SourceText.From(source, Encoding.UTF8));
        }

        /// <summary>
        /// Phase D: Scan for types in the WolverineHandlers namespace that inherit from
        /// Wolverine.Runtime.Handlers.MessageHandler. These are pre-generated handler types
        /// emitted by Wolverine's code generation step (dotnet run -- codegen).
        /// </summary>
        private static List<(string FullName, string ClassName)> FindPreGeneratedHandlerTypes(Compilation compilation)
        {
            var result = new List<(string, string)>();

            var messageHandlerSymbol = compilation.GetTypeByMetadataName(MessageHandlerFullName);
            if (messageHandlerSymbol == null) return result;

            // Scan all types in the compilation's source assembly
            var visitor = new PreGeneratedHandlerVisitor(messageHandlerSymbol, result);
            visitor.Visit(compilation.Assembly.GlobalNamespace);

            return result;
        }

        /// <summary>
        /// Phase E: Scan for types implementing IWolverineExtension or decorated with
        /// [WolverineModule] attribute in the compilation's source assembly.
        /// </summary>
        private static List<string> FindExtensionTypes(Compilation compilation)
        {
            var result = new List<string>();

            var extensionInterfaceSymbol = compilation.GetTypeByMetadataName(IWolverineExtensionFullName);
            if (extensionInterfaceSymbol == null) return result;

            var visitor = new ExtensionTypeVisitor(extensionInterfaceSymbol, result);
            visitor.Visit(compilation.Assembly.GlobalNamespace);

            return result;
        }

        private static string EmitTypeLoaderSource(
            List<string> handlerTypes,
            Dictionary<string, string> messageTypes,
            List<(string FullName, string ClassName)> preGenHandlerTypes,
            List<string> extensionTypes)
        {
            var sb = new StringBuilder();
            var hasPreGenHandlers = preGenHandlerTypes.Count > 0;

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("// Generated by Wolverine.SourceGeneration");
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Wolverine.Attributes;");
            sb.AppendLine("using Wolverine.Runtime;");
            sb.AppendLine();
            sb.AppendLine("[assembly: WolverineTypeManifest(typeof(Wolverine.Generated.GeneratedWolverineTypeLoader))]");
            sb.AppendLine();
            sb.AppendLine("namespace Wolverine.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Source-generated implementation of IWolverineTypeLoader that provides");
            sb.AppendLine("    /// compile-time handler and message type discovery, eliminating runtime");
            sb.AppendLine("    /// assembly scanning during Wolverine startup.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    internal sealed class GeneratedWolverineTypeLoader : IWolverineTypeLoader");
            sb.AppendLine("    {");

            // DiscoveredHandlerTypes
            sb.AppendLine("        private static readonly IReadOnlyList<Type> _handlerTypes = new Type[]");
            sb.AppendLine("        {");
            foreach (var handler in handlerTypes)
            {
                sb.AppendLine($"            typeof({handler}),");
            }
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public IReadOnlyList<Type> DiscoveredHandlerTypes => _handlerTypes;");
            sb.AppendLine();

            // DiscoveredMessageTypes
            sb.AppendLine("        private static readonly IReadOnlyList<(Type MessageType, string Alias)> _messageTypes = new (Type, string)[]");
            sb.AppendLine("        {");
            foreach (var kvp in messageTypes)
            {
                sb.AppendLine($"            (typeof({kvp.Key}), \"{kvp.Value}\"),");
            }
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public IReadOnlyList<(Type MessageType, string Alias)> DiscoveredMessageTypes => _messageTypes;");
            sb.AppendLine();

            // DiscoveredHttpEndpointTypes (not yet implemented)
            sb.AppendLine("        public IReadOnlyList<Type> DiscoveredHttpEndpointTypes => Array.Empty<Type>();");
            sb.AppendLine();

            // Phase E: DiscoveredExtensionTypes
            if (extensionTypes.Count > 0)
            {
                sb.AppendLine("        private static readonly IReadOnlyList<Type> _extensionTypes = new Type[]");
                sb.AppendLine("        {");
                foreach (var ext in extensionTypes)
                {
                    sb.AppendLine($"            typeof({ext}),");
                }
                sb.AppendLine("        };");
                sb.AppendLine();
                sb.AppendLine("        public IReadOnlyList<Type> DiscoveredExtensionTypes => _extensionTypes;");
            }
            else
            {
                sb.AppendLine("        public IReadOnlyList<Type> DiscoveredExtensionTypes => Array.Empty<Type>();");
            }
            sb.AppendLine();

            // Phase D: HasPreGeneratedHandlers and PreGeneratedHandlerTypes
            sb.AppendLine($"        public bool HasPreGeneratedHandlers => {(hasPreGenHandlers ? "true" : "false")};");
            sb.AppendLine();

            if (hasPreGenHandlers)
            {
                sb.AppendLine("        private static Dictionary<string, Type>? _preGenTypes;");
                sb.AppendLine();
                sb.AppendLine("        public IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes => _preGenTypes ??= BuildPreGenTypes();");
                sb.AppendLine();
                sb.AppendLine("        private static Dictionary<string, Type> BuildPreGenTypes()");
                sb.AppendLine("        {");
                sb.AppendLine($"            var dict = new Dictionary<string, Type>({preGenHandlerTypes.Count});");
                foreach (var (fullName, className) in preGenHandlerTypes)
                {
                    sb.AppendLine($"            dict[\"{className}\"] = typeof({fullName});");
                }
                sb.AppendLine("            return dict;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        public Type? TryFindPreGeneratedType(string typeName)");
                sb.AppendLine("        {");
                sb.AppendLine("            var types = PreGeneratedHandlerTypes;");
                sb.AppendLine("            if (types != null && types.TryGetValue(typeName, out var type))");
                sb.AppendLine("            {");
                sb.AppendLine("                return type;");
                sb.AppendLine("            }");
                sb.AppendLine("            return null;");
                sb.AppendLine("        }");
            }
            else
            {
                sb.AppendLine("        public IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes => null;");
                sb.AppendLine();
                sb.AppendLine("        public Type? TryFindPreGeneratedType(string typeName) => null;");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Intermediate result from the syntax/semantic analysis phase.
    /// </summary>
    internal sealed class DiscoveredType
    {
        public DiscoveredType(string fullName, string className, string namespaceName, bool isHandler, bool isMessage)
        {
            FullName = fullName;
            ClassName = className;
            NamespaceName = namespaceName;
            IsHandler = isHandler;
            IsMessage = isMessage;
        }

        public string FullName { get; }
        public string ClassName { get; }
        public string NamespaceName { get; }
        public bool IsHandler { get; }
        public bool IsMessage { get; }

        /// <summary>
        /// Message types discovered from handler method first parameters.
        /// (FullTypeName, Alias)
        /// </summary>
        public List<(string FullTypeName, string Alias)> MethodMessageTypes { get; } = new List<(string, string)>();
    }

    /// <summary>
    /// Phase D: Visits all namespaces in the compilation to find types in the
    /// WolverineHandlers namespace that inherit from MessageHandler.
    /// These represent pre-generated handler code from Wolverine's codegen step.
    /// </summary>
    internal sealed class PreGeneratedHandlerVisitor : SymbolVisitor
    {
        private readonly INamedTypeSymbol _messageHandlerSymbol;
        private readonly List<(string FullName, string ClassName)> _result;

        public PreGeneratedHandlerVisitor(
            INamedTypeSymbol messageHandlerSymbol,
            List<(string FullName, string ClassName)> result)
        {
            _messageHandlerSymbol = messageHandlerSymbol;
            _result = result;
        }

        public override void VisitNamespace(INamespaceSymbol symbol)
        {
            foreach (var member in symbol.GetMembers())
            {
                member.Accept(this);
            }
        }

        public override void VisitNamedType(INamedTypeSymbol symbol)
        {
            // Only consider types in a namespace ending with WolverineHandlers
            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
            if (!ns.EndsWith(WolverineTypeManifestGenerator.WolverineHandlersNamespaceConst))
                return;

            // Must not be abstract and must inherit from MessageHandler
            if (symbol.IsAbstract) return;
            if (!InheritsFrom(symbol, _messageHandlerSymbol)) return;

            var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            _result.Add((fullName, symbol.Name));
        }

        private static bool InheritsFrom(INamedTypeSymbol symbol, INamedTypeSymbol baseType)
        {
            var current = symbol.BaseType;
            while (current != null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                    return true;
                current = current.BaseType;
            }
            return false;
        }
    }

    /// <summary>
    /// Phase E: Visits all namespaces in the compilation to find concrete types
    /// implementing IWolverineExtension.
    /// </summary>
    internal sealed class ExtensionTypeVisitor : SymbolVisitor
    {
        private readonly INamedTypeSymbol _extensionInterfaceSymbol;
        private readonly List<string> _result;

        public ExtensionTypeVisitor(
            INamedTypeSymbol extensionInterfaceSymbol,
            List<string> result)
        {
            _extensionInterfaceSymbol = extensionInterfaceSymbol;
            _result = result;
        }

        public override void VisitNamespace(INamespaceSymbol symbol)
        {
            foreach (var member in symbol.GetMembers())
            {
                member.Accept(this);
            }
        }

        public override void VisitNamedType(INamedTypeSymbol symbol)
        {
            // Must be a concrete, non-abstract class
            if (symbol.IsAbstract) return;
            if (symbol.TypeKind != TypeKind.Class) return;
            if (symbol.DeclaredAccessibility != Accessibility.Public &&
                symbol.DeclaredAccessibility != Accessibility.Internal) return;

            // Check if it implements IWolverineExtension
            foreach (var iface in symbol.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, _extensionInterfaceSymbol))
                {
                    var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    _result.Add(fullName);
                    return;
                }
            }
        }
    }
}
