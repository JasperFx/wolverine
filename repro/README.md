# Reproductions

Self-verifying [file-based apps](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/sdk#file-based-apps).
Each prints `PASS`/`FAIL` per assertion and exits non-zero on failure:

```bash
dotnet run repro/kebab-cased-route-parameter.cs
```

`Directory.Build.props` is intentionally empty — it stops the repo-wide
props (multi-targeting, warnings-as-errors, versioning) from reaching these
single-file apps.

## kebab-cased-route-parameter.cs

`[FromRoute(Name = "journey-id")]` on an endpoint method parameter. Against
`main` the first assertion fails with `0`, because the attribute's name was
ignored and the argument was bound from the query string instead.
