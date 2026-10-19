using ProjectManagement.Api;

namespace ProjectManagement.Tests;

/// <summary>
/// Creating a project starts an event stream: the project, its admin, and one event per team member.
/// The endpoint is a plain Minimal API <c>MapPost</c>, so the When step goes through
/// <see cref="CritterStackSpec.WhenActing" /> and the spec asserts what the request appended.
/// </summary>
[BobcatFeature("Project Management")]
public class ProjectSpecs(ProjectManagementHost app) : ProjectManagementSpec(app)
{
    private Guid _projectId;
    private int _lastStatusCode;

    [BobcatSpec]
    public async Task create_a_project()
    {
        await WhenAProjectIsCreated("My Project", "ada", "alice", "bob");

        ThenTheResponseStatusIs(200);
        ThenTheProjectIdIsReturned();
        ThenEvents(typeof(ProjectCreated), typeof(AdminAssigned), typeof(TeamMemberAssigned), typeof(TeamMemberAssigned));
        ThenEmitted(new ProjectCreated("My Project"), new TeamMemberAssigned("alice"), new TeamMemberAssigned("bob"));
        await ThenStreamIsStarted<Project>(_projectId);
    }

    public Task WhenAProjectIsCreated(string name, string admin, params string[] teamMembers)
        => WhenActing($"a project named \"{name}\" is created with admin {admin} and team {string.Join(", ", teamMembers)}",
            async _ =>
            {
                var result = await PostJsonAsync<CreateProject, Guid>(
                    "/api/project/create", new CreateProject(name, admin, teamMembers));

                _lastStatusCode = result.StatusCode;
                _projectId = result.Body;
            });

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    /// <summary>The id the endpoint answers with is the id of the stream it started, not merely some Guid.</summary>
    [Then("the project id is returned")]
    public void ThenTheProjectIdIsReturned()
        => SpecAssert.Check("project id", _projectId, TheStartedStream<Project>());
}
