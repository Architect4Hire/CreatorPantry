# External Integrations

External systems are replaceable adapters. Core entities do not contain provider-specific fields.

## Gateway rules

- One typed gateway per provider boundary, behind an application-owned interface.
- Map provider DTOs immediately; do not leak SDK types into Business or ServiceModels.
- Use configured `HttpClient`, timeouts, retry/backoff only for safe operations, circuit breaking, and structured telemetry.
- Respect documented rate-limit headers and `Retry-After`.
- Persist provider cursors, external references, and synchronization status in dedicated integration records.

## Secrets and webhooks

- OAuth tokens, API keys, webhook secrets, and refresh tokens are credentials: encrypt at rest, never return them, mask administration views, and redact logs.
- Verify inbound webhook signatures before parsing business payloads.
- Store a provider event id and make processing idempotent.
- Outbound webhooks/publishing use durable delivery records with retry state and a dead-letter/manual-recovery path.

## Data ownership

- Imports create traceable source records and do not silently overwrite canonical creator content.
- Provider deletion/disconnection behavior is explicit: revoke credentials, stop sync, and define whether imported creator-owned artifacts remain.
- Provider availability must not prevent creators from reading already-owned content.

Add provider-specific rules only after a provider is selected and its current first-party documentation has been reviewed.

