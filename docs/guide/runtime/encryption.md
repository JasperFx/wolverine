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

> **Configuration order is order-insensitive.**
> `UseSystemTextJsonForSerialization` and `UseNewtonsoftForSerialization` only
> replace the default serializer when its content-type is `application/json`,
> so calling them after `UseEncryption` is a no-op against the default and
> leaves the encrypting serializer in place. Calling `UseEncryption` more than
> once throws — configure encryption exactly once during host setup.

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

`Encrypt<T>()` is symmetric: outgoing messages of type `T` are encrypted, and
inbound messages of type `T` MUST arrive encrypted (see
[Receive-side enforcement](#receive-side-enforcement) below).

Per-endpoint (sender-side):

```csharp
opts.RegisterEncryptionSerializer(provider);
opts.PublishAllMessages().ToRabbitExchange("sensitive").Encrypted();
```

Per-listener (receive-side):

```csharp
opts.UseEncryption(provider);
opts.ListenAtPort(5500).RequireEncryption();
```

`RequireEncryption()` marks a listener as accepting only encrypted envelopes.
It is the receive-side counterpart to the sender-side `.Encrypted()` extension.
The two are intentionally named differently because subscribers and listeners
have different configuration surfaces, and the asymmetric naming prevents
method-shadowing on `LocalQueueConfiguration` (which is both a subscriber and
a listener).

Both per-type and per-endpoint require `RegisterEncryptionSerializer(provider)`
(or `UseEncryption(provider)`) earlier in the same configuration so the
encrypting serializer is registered with the runtime.

Selection precedence on send: per-type > endpoint > global default. Per-type
rules run after per-endpoint rules in the runtime pipeline, so a per-type
marker takes effect last and wins. For the encryption feature specifically
this distinction is moot — both per-type `Encrypt<T>()` and per-endpoint
`Encrypted()` swap to the same encrypting-serializer instance, so the
resulting envelope is the same regardless of which marker fired last. The
distinction matters if you write your own envelope rules that compete with
the built-in ones.

### Receive-side enforcement

By default, receive-side dispatch is content-type-driven: any envelope
arriving with `application/wolverine-encrypted+json` is decrypted; envelopes
with other content-types are deserialized normally. This preserves mixed-mode
configurations and rolling-deploy scenarios where some senders have not yet
been upgraded.

When a type is marked via `Policies.ForMessagesOfType<T>().Encrypt()` OR a
listener is marked via `.RequireEncryption()`, inbound envelopes for that
type/listener that arrive without encryption (content-type ≠
`application/wolverine-encrypted+json`) are routed to the dead-letter queue
with `EncryptionPolicyViolationException`. No bytes are ever passed to a
serializer for a forged plaintext envelope. Either marker is sufficient
on its own.

## Key rotation

Static `DefaultKeyId`. Rotate by deploying a new provider with the new
key-id alongside the old keys:

1. Add the new key under `key-2025-q1`, keep `key-2024-q4` listed.
2. Update `DefaultKeyId` to `key-2025-q1`.
3. Deploy. New outgoing messages encrypt under `key-2025-q1`; in-flight or
   replayed messages with `key-2024-q4` still decrypt.
4. After the longest plausible message lifetime, drop `key-2024-q4` on a
   follow-up deploy.

## Integrity guarantees and header-leak caveat

The message body is encrypted with AES-256-GCM (confidentiality + integrity).

`MessageType`, the encryption `key-id` header, and the inner-content-type
header are *not* encrypted, but they ARE bound into the AEAD tag as
associated authenticated data. Tampering any of those three on the wire
causes decryption to fail; the envelope goes to DLQ as
`MessageDecryptionException`. This blocks cross-handler attacks where an
attacker re-stamps a legitimately encrypted envelope with a different
`MessageType` to route the decrypted body into the wrong handler.

`CorrelationId`, `SagaId`, `TenantId`, and any custom headers are NEITHER
encrypted NOR integrity-protected — brokers may need them for routing and
they can vary in transit.

> **Rule:** if a value is sensitive, put it in the message body, not in
> headers.

> **Operator note:** a `MessageDecryptionException` on a known-good
> ciphertext can mean either body tampering OR routing-metadata tampering
> (`MessageType` swap attack).

## Error handling

The encrypting serializer and receive-side guard raise three distinct
exception types on receive:

- `EncryptionKeyNotFoundException` — missing or unknown `key-id` header,
  or the key provider could not resolve the key.
- `MessageDecryptionException` — GCM tag mismatch (body tampering or
  routing-metadata tampering) or malformed body. Always poison: tampered
  or corrupted ciphertext will not decrypt on retry.
- `EncryptionPolicyViolationException` — an envelope arrived without
  encryption but the receiving message type or listener has been marked
  as requiring it. Raised by the receive-side guard before any serializer
  runs; no bytes are interpreted.

All three extend `MessageEncryptionException` for users who want to match
any of them.

> **Note on retry policies:** all three exception types are raised before
> handler dispatch (deserialization or the receive-side guard), so Wolverine's
> pipeline routes them directly to the dead-letter queue — user `OnException<>`
> retry rules do not apply to them in the current runtime. If your provider is
> a remote KMS that can have transient outages, consider implementing the
> retry/backoff inside your `IKeyProvider` rather than relying on Wolverine's
> failure policies.

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
