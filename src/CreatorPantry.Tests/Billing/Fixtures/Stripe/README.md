# Stripe contract fixtures

Stripe API objects replayed by `StripeBillingGatewayTests` (AP.6.1), so the payment gateway is tested against the
shapes Stripe documents rather than shapes written to fit the code.

**Transcribed, not recorded.** No Stripe account was available when these were made, so there is no response
here that Stripe sent this project. The 200 bodies are the example objects in Stripe's API reference, copied
byte-for-byte on **2026-10-28** from docs.stripe.com, at the API version the gateway pins (`2026-09-30.endive`).
The error bodies are **authored**: each follows the error object Stripe documents (`docs.stripe.com/api/errors` —
`type`, `code`, `message`, `param`, `doc_url`, `request_log_url`) with an error code from Stripe's error-code list.
None of them holds a real key, customer or session.

| File | Request it answers | Status | Source |
|---|---|---|---|
| `customer.stripe.json` | `POST v1/customers` | 200 | API reference, "Create a customer", example response |
| `checkout-session.stripe.json` | `POST v1/checkout/sessions` | 200 | API reference, "Create a Checkout Session", example response |
| `portal-session.stripe.json` | `POST v1/billing_portal/sessions` | 200 | API reference, "Create a portal session", example response |
| `error-resource-missing.stripe.json` | a price this account does not have | 400 | Authored |
| `error-authentication.stripe.json` | a key that is not one | 401 | Authored |
| `error-rate-limit.stripe.json` | too many requests | 429 | Authored |
| `error-idempotency-in-use.stripe.json` | the same idempotency key still executing | 409 | Authored |
| `error-api.stripe.json` | a failure on Stripe's side | 500 | Authored |

**One edit to a transcribed body:** Stripe's checkout example was made with no customer, so its `customer` is
`null`; it is set here to `cus_NffrFeUfNV2Hib` (the customer example's id), because the gateway refuses a session
that is not for the customer it asked about. Every other byte is Stripe's. The portal example is unedited — its
tests ask for the customer it names.

The checkout example is a `payment`-mode session. The gateway reads only `id`, `url`, `customer`, `livemode` and
`expires_at`, which a `subscription`-mode session has in the same places, so the one example answers both
checkouts.

## Re-recording

Once a test-mode key exists, replace each 200 with what Stripe returns for the gateway's own request, and the
errors with real ones where a test account can produce them (a missing price, a revoked restricted key). Strip
nothing but the `url` secrets if the session is still open, update the table above, and keep the version the
gateway pins. A change of `StripeBillingOptions.ApiVersion` is a re-recording, not an edit.
