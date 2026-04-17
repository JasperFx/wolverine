# GreeterWithGrpcErrors

End-to-end sample for Wolverine's **rich gRPC status details** — the gRPC
counterpart to `Wolverine.Http`'s `ProblemDetails` flow.

The server exposes two RPCs that exercise the two supported error paths:

| RPC        | Triggered error                    | Rich payload                         |
| ---------- | ---------------------------------- | ------------------------------------ |
| `Greet`    | `FluentValidation.ValidationException` | `google.rpc.BadRequest` + `FieldViolation`s |
| `Farewell` | `GreetingForbiddenException` (domain) | `google.rpc.PreconditionFailure`     |

Both surface as `RpcException` on the client with a `google.rpc.Status`
packed into the `grpc-status-details-bin` trailer.

## Projects

- **Messages** — code-first `[ServiceContract]` plus DTOs and the FluentValidation rules.
- **Server** — ASP.NET Core + Wolverine host that wires `UseFluentValidation`,
  `UseGrpcRichErrorDetails` (with an inline `MapException` for the domain exception),
  and `UseFluentValidationGrpcErrorDetails`.
- **Client** — console app that calls both RPCs and prints the unpacked details.

## Running

In one terminal:

```sh
dotnet run --project src/Samples/GreeterWithGrpcErrors/Server
```

In another:

```sh
dotnet run --project src/Samples/GreeterWithGrpcErrors/Client
```

Expected output from the client:

```
=== Valid call ===
  -> Hello, Erik

=== Validation failure (BadRequest) ===
  gRPC status : InvalidArgument
  rich code   : InvalidArgument
  BadRequest:
    - Name: Name is required
    - Age: Age must be positive

=== Domain exception (PreconditionFailure) ===
  gRPC status : FailedPrecondition
  rich code   : FailedPrecondition
  PreconditionFailure:
    - [policy.banned_name] voldemort: name is on the banned list
```

## Where to look in the code

- `Server/Program.cs` — `UseGrpcRichErrorDetails(cfg => cfg.MapException<GreetingForbiddenException>(...))`
  plus `UseFluentValidationGrpcErrorDetails()`.
- `Client/Program.cs` — `RpcException.GetRpcStatus()` then `status.Details[i].Unpack<BadRequest>()`
  / `Unpack<PreconditionFailure>()`.
