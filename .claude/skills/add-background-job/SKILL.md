---
name: add-background-job
description: Add durable CreatorPantry work for AI generation, media processing, import, embedding, export, or publishing.
---

# Add a Background Job

Use a job when work is slow, scheduled, provider-dependent, retryable, or must survive request termination.

1. Define immutable job input containing record ids—not full private content or credentials—and a validated workspace id captured by trusted code.
2. Persist job/operation state atomically with the initiating transaction or through an outbox.
3. Worker resolves workspace context and calls a facade/application boundary.
4. Define states, attempt count, next attempt, lease/lock, heartbeat if needed, cancellation, and terminal error summary.
5. Make handlers idempotent. Use stable operation/provider keys and checkpoints for multi-stage work.
6. Retry transient failures with bounded backoff and jitter. Route permanent or exhausted failures to `NeedsAttention`/dead-letter recovery.
7. Emit traces linking initiating request, operation, job, provider calls, and resulting artifact.
8. Redact payloads and secrets from logs.

Test duplicate delivery, worker crash after side effect, cancellation, lease expiry, poison input, cross-workspace tampering, and recovery.

