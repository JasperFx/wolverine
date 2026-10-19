using JasperFx.Events;
using Wolverine.Http;

namespace ProjectManagement.Api;

public record ProjectCreated(string Name);

public record CreateProject(string Name, string Admin, string[] TeamMembers);

public record ProjectCreation : CreationResponse<Guid>
{
    public ProjectCreation(string Url, Guid ProjectId) : base(Url, ProjectId)
    {
    }
}

public record ProjectCompleted;
public record AdminAssigned(string UserName);
public record TeamMemberAssigned(string UserName);
public record TeamMemberUnAssigned(string UserName);

public enum ProjectStatus
{
    Ready,
    InProgress,
    Completed
}

// Narrow view of the project that's just good enough for some
// commands and searching
public record ProjectDetails(Guid Id, string Name, ProjectStatus Status)
{
    public ProjectDetails(IEvent<ProjectCreated> created) : this(created.Id, created.Data.Name, ProjectStatus.Ready)
    {
        
    }

    public ProjectDetails Apply(ProjectCompleted completed) 
        => this with { Status = ProjectStatus.Completed };
}

// Full view of the Project, but more expensive in the end to keep up
public class Project
{
    public Guid Id { get; set; }
    
    public ProjectStatus Status { get; set; }
    
    public string Name { get; set; }
    
    public string AdminName { get; set; }

    public List<string> TeamMembers { get; set; } = new();
}

