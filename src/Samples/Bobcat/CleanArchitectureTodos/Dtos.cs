namespace CleanArchitectureTodos;

// Shared response DTOs
public record TodoListDto(int Id, string Title, string Colour, IReadOnlyList<TodoItemDto> Items);
public record TodoItemDto(Guid Id, string Title, string? Note, int Priority, bool Done);
public record TodosVm(IReadOnlyList<LookupDto> PriorityLevels, IReadOnlyList<ColourDto> Colours, IReadOnlyList<TodoListDto> Lists);
public record LookupDto(int Id, string Title);
public record ColourDto(string Code, string Name);
