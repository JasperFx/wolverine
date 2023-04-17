Wolverine
======

[![Discord](https://img.shields.io/discord/1074998995086225460?color=blue&label=Chat%20on%20Discord)](https://discord.gg/WMxrvegf8H)

Wolverine is a *Next Generation .NET Mediator and Message Bus*. Check out
the [documentation website at https://wolverine.netlify.app](https://wolverine.netlify.app).

## Help us keep working on this project 💚

[Become a Sponsor on GitHub](https://github.com/sponsors/JasperFX)

## Working with the Code

To work with the code, just open the `wolverine.sln` file in the root of the repository and go. If you want to run
integration tests though, you'll want Docker installed locally
and to start the matching testing services with:

```bash
docker compose up -d
```

There's a separate README in the Azure Service Bus tests as those require an actual cloud set up (sorry, but blame
Microsoft for not having a local Docker based emulator ala Localstack).

## Contributor's Guide

For contributors, there's a light naming style Jeremy refuses to let go of that he's used for *gulp* 20+ years:

1. All public or internal members should be Pascal cased
2. All private or protected members should be Camel cased
3. Use `_` as a prefix for private fields

The build is scripted out with [Bullseye](https://github.com/adamralph/bullseye) in the `/build` folder. To run the
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


