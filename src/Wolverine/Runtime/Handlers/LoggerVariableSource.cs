using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Handlers;

public class LoggerVariableSource : IVariableSource
{
    private readonly InjectedField _field;
    private readonly Type _loggerType;

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