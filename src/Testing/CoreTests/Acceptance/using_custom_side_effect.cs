using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class using_custom_side_effect
{
    [Fact]
    public async Task use_custom_side_effect()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        await host.InvokeMessageAndWaitAsync(new RecordText(Guid.NewGuid(), "some text"));
    }
}

#region sample_RecordTextHandler

// An options class
public class PathSettings
{
    public string Directory { get; set; } 
        = Environment.CurrentDirectory.AppendPath("files");
}

public record RecordText(Guid Id, string Text);

public class RecordTextHandler
{
    public WriteFile Handle(RecordText command)
    {
        return new WriteFile(command.Id + ".txt", command.Text);
    }
}

#endregion

#region sample_WriteFile

// ISideEffect is a Wolverine marker interface
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
    public Task ExecuteAsync(PathSettings settings)
    {
        if (!Directory.Exists(settings.Directory))
        {
            Directory.CreateDirectory(settings.Directory);
        }
        
        return File.WriteAllTextAsync(Path, Contents);
    }
}

#endregion