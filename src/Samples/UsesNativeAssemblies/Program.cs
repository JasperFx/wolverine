using JasperFx;
using Microsoft.Extensions.Hosting;
using Wolverine;

return await Host.CreateDefaultBuilder()
    .UseWolverine()
    .RunJasperFxCommands(args);