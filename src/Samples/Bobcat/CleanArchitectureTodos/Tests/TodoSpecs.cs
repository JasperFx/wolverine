namespace CleanArchitectureTodos.Tests;

/// <summary>
/// The todo list and todo item endpoints, driven over HTTP. Reuses the host's own request, document and
/// view-model types via the project reference rather than re-declaring them locally.
/// </summary>
/// <remarks>
/// Every "stored list" assertion reads the list back over HTTP rather than trusting what the write
/// echoed, so a write that answered the right status but never reached Marten still fails.
/// </remarks>
[BobcatFeature("Clean Architecture Todos")]
public class TodoSpecs(CleanArchitectureTodosHost app) : CleanArchitectureTodosSpec(app)
{
    // The first list a spec created -- the one the unqualified steps ("the stored list", "the todo list
    // is deleted") refer to. Lists created later in the same spec are reached by title.
    private int _listId;
    private readonly Dictionary<string, int> _listIdsByTitle = new();
    private Guid _itemId;
    private int _lastStatusCode;
    private IReadOnlyList<TodoListDto> _lists = [];

    // ---- lists ------------------------------------------------------------------------------

    [BobcatSpec]
    public async Task create_a_todo_list()
    {
        await WhenATodoListIsCreated("My List");

        ThenTheResponseStatusIs(201);
        await ThenTheStoredListIsTitled("My List");
    }

    [BobcatSpec]
    public async Task new_todo_list_has_default_colour()
    {
        await WhenATodoListIsCreated("Colour List");

        await ThenTheStoredListHasTheColour("#808080");
    }

    [BobcatSpec]
    public async Task create_duplicate_title_returns_400()
    {
        await GivenATodoList("Duplicate");

        await WhenATodoListIsCreated("Duplicate");

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task update_a_todo_list()
    {
        await GivenATodoList("Old Title");

        await WhenTheListIsRetitled("New Title");

        ThenTheResponseStatusIs(200);
        await ThenTheStoredListIsTitled("New Title");
    }

    [BobcatSpec]
    public async Task update_to_duplicate_title_returns_400()
    {
        await GivenATodoList("First");
        await GivenATodoList("Second");

        await WhenTheNamedListIsRetitled("Second", "First");

        ThenTheResponseStatusIs(400);
        await ThenTheStoredListIsStillTitled("Second", "Second");
    }

    [BobcatSpec]
    public async Task delete_a_todo_list()
    {
        await GivenATodoList("To Delete");

        await WhenTheTodoListIsDeleted();

        ThenTheResponseStatusIs(204);
        await ThenTheListNoLongerExists();
    }

    [BobcatSpec]
    public async Task get_all_todo_lists()
    {
        await GivenATodoList("List A");
        await GivenATodoList("List B");

        await WhenAllTodoListsAreRead();

        ThenListsAreReturned(2);
    }

    // ---- items ------------------------------------------------------------------------------

    [BobcatSpec]
    public async Task create_a_todo_item()
    {
        await GivenATodoList("Item List");

        await WhenATodoItemIsCreated("Do something");

        ThenTheResponseStatusIs(201);
        await ThenTheStoredListHasAnItemTitled("Do something");
    }

    [BobcatSpec]
    public async Task update_a_todo_item()
    {
        await GivenATodoList("Update Item List");
        await GivenATodoItem("Original");

        await WhenTheTodoItemIsRetitled("Updated");

        ThenTheResponseStatusIs(204);
        await ThenTheStoredListHasAnItemTitled("Updated");
    }

    [BobcatSpec]
    public async Task delete_a_todo_item()
    {
        await GivenATodoList("Delete Item List");
        await GivenATodoItem("To Delete");

        await WhenTheTodoItemIsDeleted();

        ThenTheResponseStatusIs(204);
        await ThenTheStoredListHasItems(0);
    }

    // ---- list steps -------------------------------------------------------------------------

    [Given("a todo list titled \"{title}\"")]
    public Task GivenATodoList(string title) => createTodoList(title);

    [When("a todo list titled \"{title}\" is created")]
    public Task WhenATodoListIsCreated(string title) => createTodoList(title);

    private async Task createTodoList(string title)
    {
        // Colour is null so the spec about the default colour exercises the host's default, not one
        // the spec supplied.
        var result = await PostJsonAsync<CreateTodoListRequest, TodoList>(
            "/api/todolists", new CreateTodoListRequest(title, Colour: null));

        _lastStatusCode = result.StatusCode;

        // Only take the id from a success. A 400 ProblemDetails body deserializes into a TodoList just
        // fine (unknown properties are ignored), so a body is not evidence that anything was created.
        if (result.StatusCode is >= 200 and < 300 && result.Body is not null)
        {
            if (_listId == 0) _listId = result.Body.Id;
            _listIdsByTitle[title] = result.Body.Id;
        }
    }

    [When("the list is retitled \"{newTitle}\"")]
    public Task WhenTheListIsRetitled(string newTitle) => retitleList(_listId, newTitle);

    [When("the list \"{title}\" is retitled \"{newTitle}\"")]
    public Task WhenTheNamedListIsRetitled(string title, string newTitle) => retitleList(_listIdsByTitle[title], newTitle);

    private async Task retitleList(int listId, string newTitle)
    {
        // PUT, and the request carries a Colour as well as the title; null leaves the stored colour alone.
        var result = await PutJsonAsync<UpdateTodoListRequest, TodoList>(
            $"/api/todolists/{listId}", new UpdateTodoListRequest(newTitle, Colour: null));

        _lastStatusCode = result.StatusCode;
    }

    [When("the todo list is deleted")]
    public async Task WhenTheTodoListIsDeleted()
    {
        var result = await DeleteAsync($"/api/todolists/{_listId}");
        _lastStatusCode = result.StatusCode;
    }

    [When("all todo lists are read")]
    public async Task WhenAllTodoListsAreRead()
    {
        // The GET returns a view model wrapping the lists alongside the priority and colour lookups.
        var result = await GetJsonAsync<TodosVm>("/api/todolists");
        _lastStatusCode = result.StatusCode;
        _lists = result.Body?.Lists ?? [];
    }

    // ---- item steps -------------------------------------------------------------------------

    [Given("a todo item titled \"{title}\"")]
    public Task GivenATodoItem(string title) => createTodoItem(title);

    [When("a todo item titled \"{title}\" is created")]
    public Task WhenATodoItemIsCreated(string title) => createTodoItem(title);

    private async Task createTodoItem(string title)
    {
        var result = await PostJsonAsync<CreateTodoItemRequest, TodoItem>(
            "/api/todoitems", new CreateTodoItemRequest(_listId, title));

        _lastStatusCode = result.StatusCode;

        // TodoItem.Id is initialised to Guid.NewGuid(), so a ProblemDetails body read as a TodoItem
        // carries a plausible id for an item that was never created.
        if (result.StatusCode is >= 200 and < 300 && result.Body is not null) _itemId = result.Body.Id;
    }

    [When("the todo item is retitled \"{newTitle}\"")]
    public async Task WhenTheTodoItemIsRetitled(string newTitle)
    {
        // The item id travels in the route; the request is (Title, Done).
        var result = await PutJsonAsync<UpdateTodoItemRequest, object>(
            $"/api/todoitems/{_itemId}", new UpdateTodoItemRequest(newTitle, Done: false));

        _lastStatusCode = result.StatusCode;
    }

    [When("the todo item is deleted")]
    public async Task WhenTheTodoItemIsDeleted()
    {
        var result = await DeleteAsync($"/api/todoitems/{_itemId}");
        _lastStatusCode = result.StatusCode;
    }

    // ---- assertions -------------------------------------------------------------------------

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    private Task<HttpResult<TodoList>> loadList(int listId) => GetJsonAsync<TodoList>($"/api/todolists/{listId}");

    [Then("the stored list is titled \"{title}\"")]
    public async Task ThenTheStoredListIsTitled(string title)
        => SpecAssert.Check("title", (await loadList(_listId)).Body?.Title, title);

    [Then("the stored list \"{createdAs}\" is still titled \"{title}\"")]
    public async Task ThenTheStoredListIsStillTitled(string createdAs, string title)
        => SpecAssert.Check("title", (await loadList(_listIdsByTitle[createdAs])).Body?.Title, title);

    [Then("the stored list has the colour \"{colour}\"")]
    public async Task ThenTheStoredListHasTheColour(string colour)
        => SpecAssert.Check("colour", (await loadList(_listId)).Body?.Colour, colour);

    [Then("the list no longer exists")]
    public async Task ThenTheListNoLongerExists()
        => SpecAssert.Check("status", (await loadList(_listId)).StatusCode, 404);

    [Then("{count} lists are returned")]
    public void ThenListsAreReturned(int count) => SpecAssert.Check("count", _lists.Count, count);

    [Then("the stored list has an item titled \"{title}\"")]
    public async Task ThenTheStoredListHasAnItemTitled(string title)
    {
        var items = (await loadList(_listId)).Body?.Items.Select(i => i.Title).ToList() ?? [];
        SpecAssert.Fact(items.Contains(title), $"The list's items are [{string.Join(", ", items)}], expected one titled '{title}'.");
    }

    [Then("the stored list has {count} items")]
    public async Task ThenTheStoredListHasItems(int count)
        => SpecAssert.Check("count", (await loadList(_listId)).Body?.Items.Count, count);
}
