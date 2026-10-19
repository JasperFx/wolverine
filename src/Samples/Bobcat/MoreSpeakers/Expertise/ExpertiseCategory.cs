namespace Expertise;

public class ExpertiseCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Sector { get; set; } = string.Empty;
    public List<string> Skills { get; set; } = [];
    public bool IsActive { get; set; } = true;
}
