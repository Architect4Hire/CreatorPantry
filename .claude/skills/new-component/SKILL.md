---
name: new-component
description: Add an Angular 22 CreatorPantry component using the installed @creator-pantry/ui package and exact repository paths.
---

# Add an Angular Component

1. If this is a routed page, say which of the three shapes it is before anything else — library, detail or flow (`src/web/DESIGN-SYSTEM.md`, "Composing a page") — and build it in that shape's order from that shape's parts. A page that fits none is a question to raise, not a fourth shape to draw. A component that is not a page says which shape it sits in and which slot.
2. Read `src/web/DESIGN-SYSTEM.md` and inspect `projects/creator-pantry-ui/src/public-api.ts` plus existing components.
3. Classify the work. Domain-neutral reusable UI belongs in `src/web/projects/creator-pantry-ui/src/lib/components/`; feature compositions belong in `src/web/src/app/features/`.
4. Reuse what `public-api.ts` exports before drawing anything; `.claude/rules/design-system.md` has one line on each. A page head, a card grid, a facts list, a toolbar field, a step list, a chip and a sliding panel all exist already, and so do the `.cp-muted` and `.cp-actions` utilities.
5. Define selector, typed signal inputs/outputs, projected content, ARIA behavior, states, responsive behavior, and theme behavior before implementation.
6. Use a standalone `cp-` component, strict templates, `OnPush`, semantic HTML, and accessible names.
7. Feature components use typed services/models and never inject `HttpClient` directly.
8. Use semantic `--cp-*` tokens only. If an intent is missing, update both themes rather than hard-coding a color.
9. Add the public export only when the component belongs to the reusable library.
10. Add a showcase example and tests for behavior/accessibility.
11. Verify light/dark themes, keyboard/focus, 200% zoom, long text, narrow layout, reduced motion, and empty/error/disabled states.
12. Run `npm run build` and `npm test` from `src/web/`, then run `@design-review`. `npm test` runs `scripts/check-page-contract.mjs` first; when it objects, fix the page rather than adding an exemption.

AI proposal components distinguish source from generated draft and expose Compare, Edit, Accept, and Discard. Media components expose processing/error, selection, rights, and alt-text state.
