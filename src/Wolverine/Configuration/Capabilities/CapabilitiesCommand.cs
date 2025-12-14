using System.Text.Json;
using JasperFx.CommandLine;
using JasperFx.Core;
using Wolverine.Tracking;

namespace Wolverine.Configuration.Capabilities;

public class CapabilitiesInput : NetCoreInput
{
    [Description("Name of the file to write the capabilities as json")]
    public string FileName { get; set; } = "wolverine.json";
}

public class CapabilitiesCommand : JasperFxAsyncCommand<CapabilitiesInput>
{
    public CapabilitiesCommand()
    {
        Usage("Write the Wolverine capabilities to wolverine.json");
        Usage("Write the Wolverine capabilities to the designated file name");
    }

    public override async Task<bool> Execute(CapabilitiesInput input)
    {
        if (input.FileName.IsEmpty())
        {
            Console.WriteLine("No file name supplied.");
            return false;
        }
        
        using var host = input.BuildHost();
        await host.StartAsync();

        var runtime = host.GetRuntime();

        var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);

        await using var stream = new FileStream(input.FileName, FileMode.Create);

        await JsonSerializer.SerializeAsync(stream, capabilities);

        await stream.FlushAsync();
        
        Console.WriteLine("Wrote Wolverine ServiceCapabilities to " + input.FileName.ToFullPath());

        return true;
    }
}