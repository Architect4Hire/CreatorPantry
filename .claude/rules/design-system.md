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

