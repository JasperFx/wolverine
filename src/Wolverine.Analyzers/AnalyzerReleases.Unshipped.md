; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
WOLV0001 | Wolverine.Handlers | Warning | A handler type or method Wolverine would discover by convention is not public
WOLV0002 | Wolverine.Sagas | Warning | [SagaIdentityFrom] names a member the saga message type does not have
WOLV0003 | Wolverine.Handlers | Warning | A handler's cascading-message tuple contains a Task or ValueTask
WOLV0004 | Wolverine.Testing | Warning | IMessageBus resolved from IHost.Services in a test project
