namespace CqrsMinimalApi.Tests;

/// <summary>
/// The Student endpoints, driven over HTTP the way a client would. Reuses the host's own Student and
/// request types via the project reference, so the specs and the endpoints cannot drift on shape.
/// </summary>
[BobcatFeature("CQRS Minimal API Students")]
public class StudentSpecs(CqrsMinimalApiHost app) : CqrsMinimalApiSpec(app)
{
    private Guid _studentId;
    private int _lastStatusCode;
    private List<Student> _students = [];

    [BobcatSpec]
    public async Task create_a_student()
    {
        await WhenAStudentIsCreated("Alice Smith", "alice@school.com");

        ThenTheResponseStatusIs(201);
        ThenTheStudentIdIsReturned();
    }

    [BobcatSpec]
    public async Task get_all_students_ordered()
    {
        await GivenAStudent("Zara", "zara@school.com");
        await GivenAStudent("Aaron", "aaron@school.com");

        await WhenAllStudentsAreRead();

        ThenAtLeastStudentsAreReturned(2);
        ThenTheyAreOrderedByName();
    }

    [BobcatSpec]
    public async Task get_student_by_id_returns_404_for_missing()
    {
        await WhenTheStudentIsRead(Guid.Empty);

        ThenTheResponseStatusIs(404);
    }

    [BobcatSpec]
    public async Task update_a_student()
    {
        await GivenAStudent("Bob Jones", "bob@school.com");

        await WhenTheStudentIsRenamed("Robert Jones");

        ThenTheResponseStatusIs(200);
    }

    [BobcatSpec]
    public async Task delete_student_and_verify_404()
    {
        await GivenAStudent("Carol White", "carol@school.com");

        await WhenTheStudentIsDeleted();
        ThenTheResponseStatusIs(204);

        await WhenTheStudentIsRead(_studentId);
        ThenTheResponseStatusIs(404);
    }

    // ---- steps ------------------------------------------------------------------------------

    [Given("a student named \"{name}\" with email \"{email}\"")]
    public Task GivenAStudent(string name, string email) => createStudent(name, email);

    [When("a student named \"{name}\" with email \"{email}\" is created")]
    public Task WhenAStudentIsCreated(string name, string email) => createStudent(name, email);

    private async Task createStudent(string name, string email)
    {
        var result = await PostJsonAsync<CreateStudentRequest, CreateStudentResponse>(
            "/api/students", new CreateStudentRequest(name, email));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null && _studentId == Guid.Empty) _studentId = result.Body.Id;
    }

    [When("all students are read")]
    public async Task WhenAllStudentsAreRead()
    {
        var result = await GetJsonAsync<List<Student>>("/api/students");
        _lastStatusCode = result.StatusCode;
        _students = result.Body ?? [];
    }

    [When("the student with id {id} is read")]
    public async Task WhenTheStudentIsRead(Guid id)
    {
        var result = await GetJsonAsync<Student>($"/api/students/{id}");
        _lastStatusCode = result.StatusCode;
    }

    [When("the student is renamed to \"{newName}\"")]
    public async Task WhenTheStudentIsRenamed(string newName)
    {
        // The POST alias for update, so the spec exercises a full round trip without committing to a
        // verb; the host accepts both POST and PUT at /api/students/{id}.
        var result = await PostJsonAsync<UpdateStudentRequest, Student>(
            $"/api/students/{_studentId}", new UpdateStudentRequest(newName));

        _lastStatusCode = result.StatusCode;
    }

    [When("the student is deleted")]
    public async Task WhenTheStudentIsDeleted()
    {
        var result = await DeleteAsync($"/api/students/{_studentId}");
        _lastStatusCode = result.StatusCode;
    }

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    [Then("the student id is returned")]
    public void ThenTheStudentIdIsReturned() => SpecAssert.Fact(_studentId != Guid.Empty, "No student id came back.");

    [Then("at least {count} students are returned")]
    public void ThenAtLeastStudentsAreReturned(int count)
        => SpecAssert.Fact(_students.Count >= count, $"Expected at least {count} students, got {_students.Count}.");

    [Then("they are ordered by name")]
    public void ThenTheyAreOrderedByName()
    {
        var names = _students.Select(s => s.Name).ToList();
        SpecAssert.Fact(names.SequenceEqual(names.OrderBy(n => n)), $"Not ordered by name: {string.Join(", ", names)}");
    }
}
