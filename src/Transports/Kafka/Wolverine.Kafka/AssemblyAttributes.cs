using JasperFx;

// Marks Wolverine.Kafka as a JasperFx module assembly so its command-line verbs (e.g. the GH-3147
// `kafka-replay` command) are discovered by the host's command runner. Wolverine.Kafka declares no
// IWolverineExtension or message handlers, so extension/handler discovery over this assembly is a no-op.
[assembly: JasperFxAssembly]
