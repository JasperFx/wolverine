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

## duplicate-fromquery-route-parameter.cs

`[FromQuery]` on a parameter whose name is also a route-template segment
(`[WolverineGet("/things/{id}")] Get([FromQuery] string id)`). Against `main`
the assertion fails with `2`: the parameter is emitted twice in the endpoint's
`ApiDescription` — once `Source=Path` (from the route template) and once
`Source=Query` (from `[FromQuery]`). Two same-name parameters are invalid
OpenAPI and crash `Microsoft.AspNetCore.OpenApi`'s XML-comment operation
transformer (`operation.Parameters.SingleOrDefault(p => p.Name == comment.Name)`
→ "Sequence contains more than one matching element"), returning HTTP 500 for
all of `/openapi/{document}.json`. This regressed between 6.16 and 6.21.
