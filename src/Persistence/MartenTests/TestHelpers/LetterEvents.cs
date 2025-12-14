using MartenTests.AggregateHandlerWorkflow;

namespace MartenTests.TestHelpers;

/// <summary>
/// Basically an ObjectMother for the A/B/C/D/Event types
/// </summary>
public static class LetterEvents
{
    public static IEnumerable<object> ToLetterEvents(this string text)
    {
        foreach (var character in text.ToLowerInvariant())
        {
            switch (character)
            {
                case 'a':
                    yield return new AEvent();
                    break;

                case 'b':
                    yield return new BEvent();
                    break;

                case 'c':
                    yield return new CEvent();
                    break;

                case 'd':
                    yield return new DEvent();
                    break;

                case 'e':
                    yield return new EEvent();
                    break;
            }
        }
    }
}