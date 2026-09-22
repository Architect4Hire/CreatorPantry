---
name: add-external-integration
description: Add an import, publishing, analytics, webhook, or other provider adapter without leaking provider concerns into the domain.
---

# Add an External Integration

1. Review current first-party provider documentation: auth, scopes, rate limits, retries, idempotency, webhooks, deletion, and terms.
2. Define an application-owned gateway interface in Domain and a provider adapter under `Integration/<Provider>/`.
3. Keep provider DTOs internal and map immediately to integration/domain records.
4. Store credentials encrypted; expose masked connection state only.
5. Configure a typed client with timeout, resilience, rate-limit handling, and redacted telemetry.
6. Model provider identifiers and cursors in mapping records, never on Recipe/ContentProject/MediaAsset.
7. For imports, preserve source/provenance and require review before overwriting canonical content.
8. For publishing, snapshot the confirmed artifact revision, use idempotency, queue the operation, and reconcile ambiguous outcomes.
9. For webhooks, verify signatures before parsing, persist event ids, and process idempotently.
10. Define disconnect, token revocation, provider deletion, local retention, and manual recovery.

Test success, auth failure, rate limit, timeout, malformed response, duplicate webhook, retry, ambiguous publish, workspace isolation, and secret redaction. Run `@integration-reviewer`.

