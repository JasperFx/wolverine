# Message Encryption

Wolverine ships with optional application-layer AES-256-GCM encryption of
message bodies. Use it when transport-level TLS is not enough — typical drivers:

- Compliance regimes (PCI-DSS, HIPAA, GDPR) that require at-rest message body
  encryption above what the broker provides.
- Hosted/shared brokers where the operator should not be able to read message
  contents from queue inspection or backups.
- Selective protection of sensitive message types (`PaymentDetails`,
  `MedicalRecord`) while keeping the rest in plain JSON for debuggability.

## Quickstart

```csharp
opts.UseEncryption(new InMemoryKeyProvider(
    defaultKeyId: "k1",
    keys: new Dictionary<string, byte[]> { ["k1"] = key32 }));
```

This encrypts every outgoing message body with AES-256-GCM under the key
registered as `k1`. Inbound messages with the encrypted content-type
(`application/wolverine-encrypted+json`) are decrypted automatically.

> **Configuration order matters.** Call `UseEncryption` (or
> `RegisterEncryptionSerializer`) **after** any
> `UseSystemTextJsonForSerialization` or `UseNewtonsoftForSerialization` call —
> those calls reset the default serializer and would silently un-install the
> encrypting one.

## The `IKeyProvider` interface

```csharp
public interface IKeyProvider
{
    string DefaultKeyId { get; }
    ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken);
}
```

Wolverine ships an `InMemoryKeyProvider` for tests and samples. For
production, write a thin adapter over your KMS — Azure Key Vault, AWS KMS,
HashiCorp Vault. Wrap it with `CachingKeyProvider`:

```csharp
opts.UseEncryption(new CachingKeyProvider(myKmsProvider, ttl: TimeSpan.FromMinutes(5)));
```

The serializer hits the provider on every send and every receive; the cache
keeps that bounded.

The byte array returned by `GetKeyAsync` is treated as a borrowed reference
owned by the provider. Callers must not mutate it or call
`CryptographicOperations.ZeroMemory` on it — doing so corrupts caching
providers like `InMemoryKeyProvider`.

## Selective encryption

Per-message-type:

```csharp
opts.RegisterEncryptionSerializer(provider);
opts.Policies.ForMessagesOfType<PaymentDetails>().Encrypt();
```

Per-endpoint:

```csharp
opts.RegisterEncryptionSerializer(provider);
opts.PublishAllMessages().ToRabbitExchange("sensitive").Encrypted();
```

Both per-type and per-endpoint require `RegisterEncryptionSerializer(provider)`
(or `UseEncryption(provider)`) earlier in the same configuration so the
encrypting serializer is registered with the runtime.

Selection precedence on send: endpoint > per-type > global default. Receive
side is fully content-type-driven: any envelope arriving with
`application/wolverine-encrypted+json` is decrypted automatically.

## Key rotation

Static `DefaultKeyId`. Rotate by deploying a new provider with the new
key-id alongside the old keys:

1. Add the new key under `key-2025-q1`, keep `key-2024-q4` listed.
2. Update `DefaultKeyId` to `key-2025-q1`.
3. Deploy. New outgoing messages encrypt under `key-2025-q1`; in-flight or
   replayed messages with `key-2024-q4` still decrypt.
4. After the longest plausible message lifetime, drop `key-2024-q4` on a
   follow-up deploy.

## Header-leak caveat

Only the message body is encrypted. `MessageType`, `CorrelationId`,
`SagaId`, `TenantId`, and any custom headers travel in cleartext — brokers
need them for routing.

> **Rule:** if a value is sensitive, put it in the message body, not in
> headers.

## Error handling

The encrypting serializer throws two distinct exception types on receive:

- `EncryptionKeyNotFoundException` — missing or unknown `key-id` header,
  or the key provider could not resolve the key.
- `MessageDecryptionException` — GCM tag mismatch or malformed body.
  Always poison: tampered or corrupted ciphertext will not decrypt on retry.

Both extend `MessageEncryptionException` for users who want to match either.

> **Note on retry policies:** because both exception types are raised during
> envelope deserialization, Wolverine's pipeline routes them directly to the
> dead-letter queue — user `OnException<>` retry rules do not apply to them
> in the current runtime. If your provider is a remote KMS that can have
> transient outages, consider implementing the retry/backoff inside your
> `IKeyProvider` rather than relying on Wolverine's failure policies.

For diagnostics, configure a logger or a sink on the dead-letter queue and
filter on `Envelope.Headers["exception-type"]` when storage is configured.

## What's not included

- **AES-CBC** — Wolverine ships GCM only. CBC requires a separate MAC for
  integrity; GCM provides authenticated encryption by construction.
- **Header encryption** — only the body is encrypted.
- **Asymmetric / per-recipient encryption** — not supported.
- **Cloud-KMS adapters** — write a thin `IKeyProvider` over your KMS;
  ready-made adapter packages may ship later.
- **Replay protection** — encryption does not prevent replay; use Wolverine's
  existing `DeduplicationId` / `MessageIdentity` if you need it.
