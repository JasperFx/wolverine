# Changelog

## Unreleased

### WolverineFx.Http

- Added native API versioning support via `Asp.Versioning.Abstractions` 10.x. Supports URL-segment versioning
  (`/v1/...`, `/v2/...`), sunset/deprecation policies with RFC 9745/8594/8288 response headers, and automatic
  OpenAPI document partitioning with Swashbuckle/Scalar/Microsoft.AspNetCore.OpenApi. No dependency on
  `Asp.Versioning.Http` — versioning is driven entirely via `IHttpPolicy`.
  See [versioning guide](docs/guide/http/versioning.md).
