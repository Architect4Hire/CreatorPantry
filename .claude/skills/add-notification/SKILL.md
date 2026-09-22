---
name: add-notification
description: Add in-app or provider-delivered workflow notifications with durable fan-out, preferences, retries, and workspace isolation.
---

# Add a Notification

The in-app notification record is the system of record. Email, team chat, webhook, or push delivery are optional provider channels added later.

1. Define a stable notification kind and workspace-scoped payload containing record ids and safe display data—not secrets.
2. Raise the notification only after the originating transaction commits, using outbox/durable work.
3. Resolve recipients at raise time from workspace membership and explicit workflow rules; persist recipient records.
4. Apply per-user/per-workspace preferences by kind and channel.
5. Queue provider delivery; never block the originating request on an optional channel.
6. Record delivery attempts, retry state, terminal failure, and provider ids.
7. Keep links relative/application-owned and reauthorize when opened.
8. Redact private recipe/content text from external-channel previews unless the user explicitly opts in.

Test duplicate events, membership changes, cross-workspace recipient leakage, preference handling, provider failure, retry exhaustion, and link authorization.

