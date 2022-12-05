using System;

namespace Wolverine.ErrorHandling.Matches;

internal
    class MessageContains : IExceptionMatch
{
    private readonly string _text;

    public MessageContains(string text)
    {
        _text = text;
    }

    public string Description => $"Exception message contains \"{_text}\"";

    public bool Matches(Exception ex)
    {
        return ex.Message.Contains(_text, StringComparison.OrdinalIgnoreCase);
    }
}