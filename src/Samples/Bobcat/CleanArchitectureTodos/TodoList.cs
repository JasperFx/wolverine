namespace CleanArchitectureTodos;

/// <summary>
/// TodoList aggregate stored as a Marten document.
/// Contains its own items — no separate TodoItem table needed.
/// </summary>
public class TodoList
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Colour { get; set; } = "#808080"; // Grey default
    public List<TodoItem> Items { get; set; } = [];

    // Audit fields
    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}

public class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Note { get; set; }
    public PriorityLevel Priority { get; set; } = PriorityLevel.None;
    public bool Done { get; set; }
}

public enum PriorityLevel
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// Supported colours for todo lists (replaces the Colour value object).
/// </summary>
public static class TodoColours
{
    public static readonly Dictionary<string, string> Supported = new()
    {
        ["#FF0000"] = "Red",
        ["#FFA500"] = "Orange",
        ["#008000"] = "Green",
        ["#008080"] = "Teal",
        ["#0000FF"] = "Blue",
        ["#800080"] = "Purple",
        ["#808080"] = "Grey",
    };

    public static bool IsValid(string code) => Supported.ContainsKey(code);
}
