# BankingService

This project shows the usage of Alba to test an HTTP endpoint of Wolverine.Http with the usage of fakes injected into the
IoC container for the application. To run the project, you first need to run the docker compose file for Wolverine at
the root directory of the wolverine codebase:

```bash
docker compose up -d
```

or you can use the build script like so for *nix machines:

```bash
./build.sh test-samples
```

or this on Windows:

```bash
build test-samples
```