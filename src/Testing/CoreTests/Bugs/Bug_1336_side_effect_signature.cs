using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_1336_side_effect_signature : IntegrationContext
{
    public Bug_1336_side_effect_signature(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task check_out_the_side_effect_signature()
    {
        await Host.InvokeMessageAndWaitAsync(new ProcessText("hey"));
    }
}

public class WriteFile : ISideEffect
{
    public string Path { get; }
    public string Contents { get; }

    public WriteFile(string path, string contents)
    {
        Path = path;
        Contents = contents;
    }

    // Wolverine will call this method.
    public Task ExecuteAsync()
    {
        return File.WriteAllTextAsync(Path, Contents);
    }
}

public record ProcessText(string Content);

public static class ProcessTextHandler
{
    public static WriteFile Handle(ProcessText message)
    {
        return new WriteFile("file.txt", message.Content);
    }
}