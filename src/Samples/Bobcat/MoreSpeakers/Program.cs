using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// GH-5703: a plain `dotnet run` of this sample has no launch settings, so it runs in Production, where Wolverine 7.0
// expects pre-generated code. A sample compiles its generated code at runtime in every environment instead
builder.Services.CritterStackDefaults(x => x.Production.GeneratedCodeMode = TypeLoadMode.Auto);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten")
        ?? "Host=localhost;Port=5433;Database=more_speakers;Username=postgres;Password=postgres";

    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "speakers";

    opts.Schema.For<Speakers.Speaker>().Index(x => x.Email);
    opts.Schema.For<Speakers.Speaker>().Index(x => x.Expertise);
    opts.Schema.For<Mentorships.Mentorship>().Index(x => x.MentorId);
    opts.Schema.For<Mentorships.Mentorship>().Index(x => x.MenteeId);
    opts.Schema.For<Mentorships.Mentorship>().Index(x => x.Status);
})
.IntegrateWithWolverine()
.UseLightweightSessions();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.Policies.AutoApplyTransactions();
    opts.UseFluentValidation();
    opts.ServiceName = "MoreSpeakers";
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});

await app.RunAsync();
