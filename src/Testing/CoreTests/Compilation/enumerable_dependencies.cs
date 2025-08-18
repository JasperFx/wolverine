using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Compilation;

public class enumerable_dependencies
{
    [Fact]
    public async Task can_use_mixed_scoping_of_array_elements()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IColor, Red>();
                opts.Services.AddSingleton<IWidget, AWidget>();
                opts.Services.AddSingleton<IWidget, BWidget>();
                opts.Services.AddScoped<IWidget, ServiceUsingWidget>();

                opts.CodeGeneration.GeneratedCodeOutputPath = AppContext.BaseDirectory
                    .ParentDirectory().ParentDirectory().ParentDirectory().AppendPath("Internal", "Generated");
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                
                
            }).StartAsync();

        await host.InvokeMessageAndWaitAsync(new WidgetUsingMessage());
        await host.InvokeMessageAndWaitAsync(new WidgetUsingMessage2());
    }
}

public record WidgetUsingMessage;
public record WidgetUsingMessage2;

public static class WidgetUsingMessageHandler
{
    public static void Handle(WidgetUsingMessage message, IEnumerable<IWidget> widgets)
    {
        // Don't really need to do anything here
        Debug.WriteLine("Got here");
    }
}

public class WidgetUsingMessage2Handler
{
    private readonly IWidget[] _widgets;

    public WidgetUsingMessage2Handler(IEnumerable<IWidget> widgets)
    {
        _widgets = widgets.ToArray();
    }


    public void Handle(WidgetUsingMessage2 message)
    {
        _widgets.Any().ShouldBeTrue();
        Debug.WriteLine("Got here");
    }
}

public interface IWidget
{
}

public interface IColor;
public class AWidget : IWidget{}
public class BWidget : IWidget{}

public class ServiceUsingWidget : IWidget
{
    public ServiceUsingWidget(IColor color)
    {
        Color = color;
    }

    public IColor Color { get; set; }
}

public class Blue
{
}

public class Green
{
}

public class Red : IColor
{
}