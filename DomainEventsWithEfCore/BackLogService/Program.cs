// See https://aka.ms/new-console-template for more information

using JasperFx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

var builder = Host.CreateApplicationBuilder();
var connectionString = builder.Configuration.GetConnectionString("postgres");

builder.UseWolverine(opts =>
{
    opts.UseEntityFrameworkCoreTransactions();
    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");
    
    opts.Policies.AutoApplyTransactions();
});

var host = builder.Build();
return await host.RunJasperFxCommands(args);