using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public static class Program
{
    public static async Task EntryPoint()
    {
        #region sample_QuickStart_Add_To_AspNetCore

        var host = await Host.CreateDefaultBuilder()
            // Adds Wolverine to your .Net Core application
            // with its default configuration
            .UseWolverine()
            .StartAsync();

        #endregion
    }
}

#region sample_QuickStart_InvoiceCreated

public class InvoiceCreated
{
    public Guid InvoiceId { get; set; }
}

public class InvoiceHandler
{
    public void Handle(InvoiceCreated created)
    {
        // do something here with the created variable...
    }
}

#endregion