namespace CqrsMinimalApi;

/// <summary>
/// Student document stored in Marten. Guid-keyed for the RESTful API
/// (POST returns 201 with the assigned id; collection routes are
/// `/api/students/{id}`).
/// </summary>
public class Student
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public bool Active { get; set; } = true;
}
