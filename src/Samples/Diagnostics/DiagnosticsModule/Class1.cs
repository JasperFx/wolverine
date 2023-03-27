using Wolverine;
using Wolverine.Attributes;

[assembly: WolverineModule]

namespace DiagnosticsModule;

public interface IDiagnosticsMessageHandler
{
}

public record DiagnosticsMessage1 : IMessage;

[WolverineMessage]
public record DiagnosticsMessage2;

public record NotDiagnosticsMessage3;

public record DiagnosticsMessage4 : IDiagnosticsMessageHandler;