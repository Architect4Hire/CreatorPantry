# CreatorPantry Angular Design System

The installed Angular 22 design system is the visual source of truth. Do not create a parallel token set or shared-component library.

## Authoritative paths

- Direction and accessibility: `src/web/DESIGN-SYSTEM.md`
- Workspace instructions: `src/web/CLAUDE.md`
- Library: `src/web/projects/creator-pantry-ui/`
- Public API: `src/web/projects/creator-pantry-ui/src/public-api.ts`
- Tokens: `src/web/projects/creator-pantry-ui/src/lib/styles/tokens.css`
- Themes: `src/web/projects/creator-pantry-ui/src/lib/styles/themes.css`
- Global styles: `src/web/projects/creator-pantry-ui/src/lib/styles/global.css`
- Showcase: `src/web/src/app/`

## Visual contract

CreatorPantry pairs editorial warmth with productivity-tool density. `DM Serif Display` is used for editorial/display moments; `DM Sans` is used for controls, labels, content, and data. Evergreen is the primary action color. Pink, orange, purple, and blue distinguish tools and secondary states without competing with primary actions.

Theme is applied with `data-cp-theme="light|dark"` on `<html>`. `CpThemeService` manages system preference and persistence. Never create separate light/dark component markup.

Consumers import the CSS foundation exactly once, in this order:

```css
@import '@creator-pantry/ui/styles/tokens.css';
@import '@creator-pantry/ui/styles/themes.css';
@import '@creator-pantry/ui/styles/global.css';
```

## Implemented public API

- `CpThemeService`
- `CpButtonComponent`: `primary | secondary | ghost | text | danger`; `sm | md | lg`; optional full width
- `CpCardComponent`: standard, interactive, and flush surfaces
- `CpBadgeComponent`: `neutral | success | pink | orange | purple | blue`
- `CpFieldComponent`: label, hint, required, error, projected native control
- `CpProgressComponent`: labeled bounded progress
- `CpDialogComponent`: modal shell, title/description, action projection, close event
- `CpQuickActionComponent`: creator-tool action card with tone and activation event
- `CpStatusPillComponent`: `neutral | progress | success | warning | error | stale`, distinct glyph per tone plus required text
- `CpListShellComponent`: domain-neutral list/table frame — heading, actions/pagination slots, loading/error/empty/ready states; no columns or data
- `CpTabsComponent` / `CpTabPanelComponent`: ARIA tabs pattern, roving tabindex, disabled tabs, lazy-mounted panels
- `CpToolbarComponent`: search/filters/actions/overflow slots, loading/disabled states, roving-tabindex keyboard nav
- `CpEmptyStateComponent`: title/description/icon, projected actions slot, `first-use | no-results` variants
- `CpUploaderComponent`: browse/drag-drop shell, per-item queued/uploading/success/error rendering, retry/cancel/remove — no transport of its own
- `CpDiffLegendComponent`: legend for `added | removed | changed | moved | unchanged | warning | selected`; presentation only, no diff computation
- `CpToastRegionComponent`: polite/assertive toast region, auto-dismiss with hover/focus pause, dedup, persistent warning/error

Use these exports from `@creator-pantry/ui`; deep imports from `src/lib` are defects.

## Extension rules

1. Classify work as token, primitive, reusable component, pattern, or feature composition.
2. Search the public API and existing source before adding anything.
3. Prefer composition over variant growth.
4. Define selector, typed signal inputs/outputs, content projection, ARIA, states, and responsive behavior first.
5. Reusable, domain-neutral UI belongs in `projects/creator-pantry-ui`; recipe/content workflows remain in feature folders.
6. Use semantic `--cp-*` tokens. If a semantic intent is missing, add it to both themes.
7. Update the showcase, documentation, and `public-api.ts` for public changes.
8. Verify WCAG 2.2 AA, keyboard operation, visible focus, screen-reader naming, 200% zoom, narrow layout, long content, both themes, and reduced motion.

Generated `dist/` output and `node_modules/` are never committed.

