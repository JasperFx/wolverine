public class Day
{
    public long Version { get; set; }

    public int Id { get; set; }

    // how many trips started on this day?
    public int Started { get; set; }

    // how many trips ended on this day?
    public int Ended { get; set; }

    public int Stops { get; set; }

    // how many miles did the active trips
    // drive in which direction on this day?
    public double North { get; set; }
    public double East { get; set; }
    public double West { get; set; }
    public double South { get; set; }
}