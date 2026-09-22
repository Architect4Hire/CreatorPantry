# Publishing

Publishing is a provider-neutral domain boundary.

## Model

Use `PublishingTarget`, `Publication`, `PublicationRevision`, `ExternalPublicationReference`, and `DeliveryAttempt` (names may be tuned). Core content entities do not receive fields such as provider post ids.

## Lifecycle

```text
Draft → Ready → Confirmed → Queued → Publishing → Published
                                  └→ Failed → Retry/NeedsAttention
```

- Confirmation captures the exact artifact revision, target, schedule, and actor/policy.
- A content edit after confirmation creates a newer revision; it does not mutate the queued payload invisibly.
- Commands are idempotent. Persist the idempotency key and provider external reference.
- Retry only operations documented as safe; ambiguous outcomes require reconciliation before another create call.
- Store a sanitized request summary, response status, timestamps, and correlation/provider ids.
- Scheduled times store UTC plus the originating workspace timezone and local intent.
- Unpublishing, updating, and deleting are distinct capabilities because providers differ.

No provider connection means CreatorPantry still supports export/download workflows. Provider downtime never blocks access to creator-owned source content.

