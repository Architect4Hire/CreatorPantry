# Authentication and Authorization

CreatorPantry is web-first. The Angular browser client uses the YARP BFF and receives only a secure session cookie. It does not receive or store bearer tokens.

## Identity

- ASP.NET Core Identity owns users, credentials, recovery, lockout, and platform role assignment.
- `PlatformAdmin` is the only initial platform-wide role.
- Workspace authorization is always derived from `WorkspaceMembership`.
- Support impersonation or administrative access requires a separate audited design; do not infer it from `PlatformAdmin`.

## Browser session

- Cookie is `HttpOnly`, `Secure`, and configured with the narrowest viable domain/path and SameSite policy.
- State-changing requests require CSRF protection.
- Redis-backed session/ticket storage is preferred when multiple gateway instances run.
- Logout invalidates the server session and clears the cookie.
- No tokens in localStorage, sessionStorage, IndexedDB, URLs, or frontend logs.

## Gateway-to-API trust (baseline B-13)

- There is no OAuth/OIDC authorization server. Do not add OpenIddict, refresh tokens, or OAuth clients without a new decision.
- The gateway owns the session and attaches a gateway-signed internal token (ES256, issuer `creatorpantry-gateway`, audience `creatorpantry-api`, lifetime ≤ 2 minutes) after stripping client-supplied credentials.
- The API validates only that token, with the gateway's public key. The private key never leaves the gateway.
- Product routes accept only `token_use=user` tokens. Internal session routes (`/api/v1/internal/**`) accept only the gateway's `token_use=service` token and are never proxied from browsers.
- Every API controller action requires an authenticated user (`MapAuthenticatedControllers`); anonymous actions opt out explicitly with `[AllowAnonymous]`.
- Machine operations use hashed, rotatable API keys on explicit `ops` routes only (B-14); they never act as a creator.

## Authorization

- Policies verify active workspace membership and the minimum membership role.
- Data-dependent authorization remains in Business.
- Return 404 when disclosure of a workspace or resource would leak its existence.
- Background jobs record the initiating user when relevant, but execute through a validated service identity and workspace context.

## Sensitive operations

Publishing, provider connection changes, member/role changes, secret rotation, destructive deletion, and automation approval require explicit policies and audit events. Consider step-up authentication when the product reaches production readiness.

