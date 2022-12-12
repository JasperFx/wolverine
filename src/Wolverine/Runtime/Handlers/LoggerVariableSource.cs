using System;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.Handlers;

internal class LoggerVariableSource : IVariableSource
{
    private readonly Type _messageType;
    private readonly Type _loggerType;
    private readonly InjectedField _field;

    public LoggerVariableSource(Type messageType)
    {
        _messageType = messageType;
        _loggerType = typeof(ILogger<>).MakeGenericType(messageType);

        _field = new InjectedField(_loggerType);
    }

    public bool Matches(Type type)
    {
        return type == typeof(ILogger) || type == _loggerType;
    }

    public Variable Create(Type type)
    {
        if (type == typeof(ILogger)) return new CastVariable(_field, typeof(ILogger));

        if (type == _loggerType) return _field;

        throw new ArgumentOutOfRangeException(nameof(type));
    }
}