using System.Diagnostics;
using Wolverine;
using Wolverine.Attributes;

[assembly: WolverineModule]

namespace DiagnosticsModule;

public interface IDiagnosticsMessageHandler;

public record DiagnosticsMessage1 : IMessage;

[WolverineMessage]
public record DiagnosticsMessage2;

public record NotDiagnosticsMessage3;

public record DiagnosticsMessage4;

public class DiagnosticsMessage4Thing : IDiagnosticsMessageHandler
{
    public void Handle(DiagnosticsMessage4 msg) => Debug.WriteLine("Got it");
}

    

