using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Handlers;

public class LoggerVariableSource : IVariableSource
{
    private readonly InjectedField _field;
    private readonly Type _loggerType;

    // Closes ILogger<> over the runtime-resolved message type so the generated
    // handler class has a typed logger field. AOT-clean apps in
    // TypeLoadMode.Static run with pre-generated handler code where the closed
    // ILogger<TMessage> is statically known; the source-generated registration
    // path doesn't construct LoggerVariableSource at runtime. This ctor is
    // only invoked by the Dynamic codegen mode, which already triggers
    // AssemblyGenerator and is documented as not-AOT-clean in the AOT guide.
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "ILogger<> closed over runtime messageType during Dynamic codegen; AOT consumers run pre-generated handler code in TypeLoadMode.Static. See AOT guide.")]
    public LoggerVariableSource(Type messageType)
    {
        _loggerType = typeof(ILogger<>).MakeGenericType(messageType);

        _field = new InjectedField(_loggerType, "loggerForMessage");
    }

    public bool Matches(Type type)
    {
        return type == typeof(ILogger) || type == _loggerType;
    }

    public Variable Create(Type type)
    {
        if (type == typeof(ILogger))
        {
            return new CastVariable(_field, typeof(ILogger));
        }

        if (type == _loggerType)
        {
            return _field;
        }

        throw new ArgumentOutOfRangeException(nameof(type));
    }
}