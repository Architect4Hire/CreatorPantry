---
name: creator-pantry-design-system
description: Build or modify CreatorPantry Angular 22 UI tokens, primitives, components, patterns, and feature compositions.
---

# CreatorPantry design-system workflow

1. Classify the work: token, primitive, component, pattern, or feature composition.
2. Inspect `public-api.ts`, existing component APIs, and semantic tokens before adding anything.
3. Prefer composition over variant growth. Add a variant only when the semantic intent repeats across features.
4. Define the contract first: selector, typed inputs, outputs, projected regions, ARIA behavior, states, and responsive behavior.
5. Implement with standalone Angular 22, signal inputs/outputs, strict templates, and OnPush.
6. Style only with semantic `--cp-*` tokens. Add a semantic token to both themes when an intent is missing.
7. Add or update a showcase example that demonstrates realistic CreatorPantry content.
8. Verify keyboard operation, visible focus, screen-reader naming, 200% zoom, light/dark mode, mobile layout, long text, and empty/error/disabled states.
9. Run `npm run build` and relevant tests. Fix failures; do not weaken strictness or budgets to silence them.
10. Update `README.md`, `DESIGN-SYSTEM.md`, and `public-api.ts` for public changes.

Never place API calls, recipe business logic, router assumptions, or application state in the UI library.
