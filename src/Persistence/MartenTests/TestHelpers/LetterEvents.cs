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
    
    public static IEnumerable<object> ToRandomEvents()
    {
        for (int i = 0; i < Random.Shared.Next(3, 15); i++)
        {
            var number = Random.Shared.Next(0, 10);
            if (number < 2)
            {
                yield return new AEvent();
            }
            else if (number < 4)
            {
                yield return new BEvent();
            }
            else if (number < 6)
            {
                yield return new CEvent();
            }
            else if (number < 8)
            {
                yield return new DEvent();
            }
            else
            {
                yield return new EEvent();
            }
        }

    }
}