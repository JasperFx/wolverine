Wolverine
======

[![Discord](https://img.shields.io/discord/1074998995086225460?color=blue&label=Chat%20on%20Discord)](https://discord.gg/WMxrvegf8H)

Wolverine is a *Next Generation .NET Mediator and Message Bus*. Check out
the [documentation website at https://wolverinefx.net/](https://wolverinefx.net/).

## Support Plans

<div align="center">
    <img src="https://www.jasperfx.net/logo.png" alt="JasperFx logo" width="70%">
</div>

While Wolverine is open source, [JasperFx Software offers paid support and consulting contracts](https://jasperfx.net)
for Wolverine.

## Help us keep working on this project 💚

[Become a Sponsor on GitHub](https://github.com/sponsors/JasperFX)

## Working with the Code

To work with the code, just open the `wolverine.slnx` file in the root of the repository and go. There's also
`wolverine_slim.slnx`, a lighter variant that omits several extension projects, and `wolverine_fsharp.slnx`. CI builds
the full `wolverine.slnx`, so build that one before pushing:

```bash
dotnet build wolverine.slnx -c Release -f net9.0
```

If you want to run integration tests, you'll want Docker installed locally
and to start the matching testing services with:

```bash
docker compose up -d
```

That covers Azure Service Bus too — it runs against the
[Azure Service Bus emulator](https://wolverinefx.net/guide/messaging/transports/azureservicebus/emulator.html),
so no cloud subscription is needed. See the [Azure Service Bus tests README](src/Transports/Azure/Wolverine.AzureServiceBus.Tests/README.md)
for the ports it uses and why there are two emulator instances.

## Branches

This repository follows a major-line branching strategy:

- **`main`** — Active development for the Wolverine 6.x line, which is generally available and shipping regular releases. Day-to-day work, new features, the cold-start / runtime-perf pass and the [AOT pillar](https://github.com/JasperFx/wolverine/issues/2746) all land here. See the [migration guide](https://wolverinefx.net/guide/migration.html#key-changes-in-6-0) for the at-a-glance table of changed defaults / removed APIs / moved namespaces from 5.x.
- **`5.0`** — Maintenance branch for the Wolverine 5.x line, branched from the `V5.39.0` tag. Receives bug fixes only — no new features and no breaking changes. Patch releases off the 5.x line ship from this branch.
- **`archive/cloudevents-attempt-2025`** — Preserved, abandoned. An incomplete CloudEvents-for-SQS-and-SNS feature branch from August 2025 that never merged. Kept for historical reference only.

Older release-specific branches (e.g., `4.0`, `release/5.30`, `5.36`) exist for in-flight or completed work on prior versions and are not active development surfaces. New contributions should target `main`. Backport candidates for the 5.x line can be opened against `5.0` after the corresponding PR has merged to `main`.

## Contributor's Guide

### Naming

For contributors, there's a light naming style Jeremy refuses to let go of that he's used for *gulp* 20+ years.
The thing to internalize is that **casing follows accessibility, not member kind**:

1. All public or internal members should be Pascal cased
2. All private or protected members should be Camel cased — including private *static* helper methods
3. Use `_` as a prefix for private fields
4. Constants are Pascal cased whatever their accessibility

So a single class routinely mixes both, and that's intentional:

```csharp
public class SqlServerQueue
{
    private readonly SqlServerTransport _parent;

    public Task SendAsync(Envelope envelope) { ... }   // public  -> Pascal

    private void buildSenderIfMissing() { ... }        // private -> camel
}
```

If you're coming from a codebase that Pascal cases every method regardless, rule 2 is the one that will catch you
out. These rules are encoded in [`.editorconfig`](./.editorconfig), so your IDE will flag new violations as you type.
They surface in the IDE and in `dotnet format`, but never fail the build — there's a fair amount of pre-existing
drift, and renaming a `protected` member is a breaking change for anyone subclassing it, so please don't mass-fix
what's already there.

### Don't edit `CHANGELOG.md`

Please leave `CHANGELOG.md` alone in a pull request. Every concurrent branch appends to the same "Unreleased"
section, so every one of them conflicts with every other on merge. The PR description and the commit message are
where a change explains itself, and both are attached to the work forever. Release notes are a release-time job.

### Building

The build is scripted out with [Nuke](https://github.com/nuke-build/nuke) in the `/build` folder. To run the
build file locally, use `build` with Windows or `./build.sh` on OSX or Linux.

## Documentation

All the documentation content is in the `/docs` folder. The documentation is built and published
with [Vitepress](https://vitepress.vuejs.org/) and
uses [Markdown Snippets](https://github.com/SimonCropp/MarkdownSnippets) for code samples. To run the documentation
locally, you'll need a recent version of Node.js installed. To start the documentation website, first run:

```bash
npm install
```

Then start the actual website with:

```bash
npm run docs
```

To update the code sample snippets, use:

```bash
npm run mdsnippets
```

## History

This is a little sad, but Wolverine started as a project named "[Jasper](https://github.com/jasperfx/jasper)" way, way
back in 2015 as an intended reboot of an even older project
named [FubuMVC / FubuTransportation](https://fubumvc.github.io) that
was a combination web api framework and asynchronous message bus. What is now Wolverine was meant to build upon what we
thought was the positive aspects of fubu's programming model but do so with a
much more efficient runtime. Wolverine was largely rebooted, revamped, and renamed in 2022 with the intention of being
combined with [Marten](https://martendb.io) into the "critter stack" for highly productive
and highly performant server side development in .NET.



<!-- ci-stabilization tracer (no-op) — post-#2584 sweep 2026-05-12 -->
