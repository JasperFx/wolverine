using Spectre.Console;
using Spectre.Console.Rendering;

namespace Wolverine.Transports;

public record PropertyColumn(string Header, string AttributeName, Justify Alignment = Justify.Left)
{
    public PropertyColumn(string Header, Justify Alignment = Justify.Left) : this(Header, Header, Alignment)
    {
    }

    public IRenderable BuildCell(Dictionary<string, string> dict)
    {
        if (dict.TryGetValue(AttributeName, out var value))
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (value != null)
            {
                return new Markup(value);
            }
        }

        return new Markup("-");
    }
}