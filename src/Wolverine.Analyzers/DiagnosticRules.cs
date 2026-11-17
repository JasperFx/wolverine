using Microsoft.CodeAnalysis;

namespace Wolverine.Analyzers;

internal static class DiagnosticRules
{
    private const string HelpRoot = "https://wolverinefx.io/guide/analyzers/";

    public static readonly DiagnosticDescriptor HandlerIsNotPublic = new(
        id: "WOLV0001",
        title: "Wolverine handler is not public",
        messageFormat: "{0} is not public, so Wolverine will not discover it as a message handler",
        category: "Wolverine.Handlers",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "Wolverine only discovers public handler types and public handler methods. A non-public Handle or Consume method, or a non-public class named like a handler, is silently skipped. Make it public, or mark it [WolverineIgnore] if it is not meant to be a handler.",
        helpLinkUri: HelpRoot + "wolv0001");

    public static readonly DiagnosticDescriptor SagaIdentityMemberNotFound = new(
        id: "WOLV0002",
        title: "Saga identity member not found on the message",
        messageFormat: "[SagaIdentityFrom(\"{0}\")] names a member that {1} does not have, so Wolverine cannot read the saga identity from it",
        category: "Wolverine.Sagas",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "[SagaIdentityFrom] tells Wolverine which public field or property of the message carries the saga identity. When the message type has no public member by that name (the comparison is case sensitive), Wolverine falls back to its other identity conventions or to the SagaId envelope header instead.",
        helpLinkUri: HelpRoot + "wolv0002");

    public static readonly DiagnosticDescriptor CascadingTupleContainsTask = new(
        id: "WOLV0003",
        title: "Cascading message tuple contains a Task or ValueTask",
        messageFormat: "The tuple returned by {0} contains a {1} element, which Wolverine would treat as a cascading message",
        category: "Wolverine.Handlers",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "Every element of a tuple returned from a Wolverine handler is a cascading message or a side effect. A Task or ValueTask in the tuple is never awaited by Wolverine. Make the method async and return the tuple from it, or await the work first.",
        helpLinkUri: HelpRoot + "wolv0003");

    public static readonly DiagnosticDescriptor MessageBusFromHostServices = new(
        id: "WOLV0004",
        title: "IMessageBus resolved from IHost.Services in a test",
        messageFormat: "Use host.MessageBus() instead of resolving IMessageBus from IHost.Services",
        category: "Wolverine.Testing",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "IMessageBus is scoped. Resolving it from the root IHost.Services gives a bus that is not attached to a message context, so tracking, outbox and context propagation do not behave the way they do inside a handler. The host.MessageBus() extension method in the Wolverine namespace builds one that is.",
        helpLinkUri: HelpRoot + "wolv0004");
}
