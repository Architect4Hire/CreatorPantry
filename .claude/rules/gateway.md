# Gateway and Browser Boundary

The browser reaches application APIs through `CreatorPantry.Gateway`. The SPA must not call the internal API directly.

- YARP routes only the paths required by the browser.
- Strip client-supplied `Authorization`, forwarding, and internal identity headers before adding trusted values.
- CORS names the configured SPA origin and allows credentials; never reflect arbitrary origins.
- Apply security headers, request-size limits, rate limits, and correlation identifiers at the edge.
- Do not cache personalized responses at a shared proxy layer.
- Upload routes use explicit size/type policy and streaming; large media should use controlled direct-to-storage flows when designed.
- Development and production preserve the same browser security model even if hosting differs.
- OpenAPI is development-only unless a concrete operational need justifies protected production access.

The gateway is not a business layer. It authenticates the browser session, enforces edge policy, and forwards to the API.

