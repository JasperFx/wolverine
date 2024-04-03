namespace TestingSupport.Compliance;

public enum Colors
{
    Red,
    Blue,
    Green
}

public class Target
{
    private static readonly Random _random = new(67);

    private static readonly string[] _strings =
    [
        "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Violet",
        "Pink", "Gray", "Black"
    ];

    private static readonly string[] _otherStrings =
    [
        "one", "two", "three", "four", "five", "six", "seven", "eight",
        "nine", "ten"
    ];

    public float Float;

    public string StringField;

    public Target()
    {
        Id = Guid.NewGuid();
        StringDict = new Dictionary<string, string>();
    }

    public Guid Id { get; set; }

    public int Number { get; set; }
    public long Long { get; set; }
    public string String { get; set; }
    public string AnotherString { get; set; }

    public Guid OtherGuid { get; set; }

    public Target Inner { get; set; }

    public Colors Color { get; set; }

    public bool Flag { get; set; }

    public double Double { get; set; }
    public decimal Decimal { get; set; }
    public DateTime Date { get; set; }
    public DateTimeOffset DateOffset { get; set; }

    public int[] NumberArray { get; set; }


    public Target[] Children { get; set; }

    public int? NullableNumber { get; set; }
    public DateTime? NullableDateTime { get; set; }
    public bool? NullableBoolean { get; set; }

    public IDictionary<string, string> StringDict { get; set; }

    public Guid UserId { get; set; }

    public static IEnumerable<Target> GenerateRandomData(int number)
    {
        var i = 0;
        while (i < number)
        {
            yield return Random(true);

            i++;
        }
    }

    public static Target Random(bool deep = false)
    {
        var target = new Target();
        target.String = _strings[_random.Next(0, 10)];
        target.AnotherString = _otherStrings[_random.Next(0, 10)];
        target.Number = _random.Next();

        target.Flag = _random.Next(0, 10) > 5;

        target.Float = float.Parse(_random.NextDouble().ToString());

        target.NumberArray = [_random.Next(0, 10), _random.Next(0, 10), _random.Next(0, 10)];

        target.NumberArray = target.NumberArray.Distinct().ToArray();

        switch (_random.Next(0, 2))
        {
            case 0:
                target.Color = Colors.Blue;
                break;

            case 1:
                target.Color = Colors.Green;
                break;

            default:
                target.Color = Colors.Red;
                break;
        }

        target.Long = 100 * _random.Next();
        target.Double = _random.NextDouble();
        target.Long = _random.Next() * 10000;


        target.Date = DateTime.Today.AddDays(_random.Next(-10000, 10000)).ToUniversalTime();

        if (deep)
        {
            target.Inner = Random();

            var number = _random.Next(1, 10);
            target.Children = new Target[number];
            for (var i = 0; i < number; i++)
            {
                target.Children[i] = Random();
            }

            target.StringDict = Enumerable.Range(0, _random.Next(1, 10))
                .ToDictionary(i => $"key{i}", i => $"value{i}");
        }

        return target;
    }
}