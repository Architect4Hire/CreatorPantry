# CreatorPantry visual system

## Direction

CreatorPantry is a focused production workspace for food bloggers and content creators—not a recipe index. The interface pairs editorial warmth with productivity-tool density. DM Serif Display gives content moments a distinctive publishing voice; DM Sans keeps controls and data highly legible. Evergreen is the action color. Pink, orange, purple, and blue distinguish tools and states without competing with primary actions.

## Layers

1. Primitive tokens define scale: spacing, type, radii, motion.
2. Semantic theme tokens define intent: background, surface, text, border, primary, status.
3. Angular primitives implement interaction and accessibility.
4. Feature compositions combine primitives for recipe editing, AI writing, image production, planning, and analytics.

## Theme contract

Set `data-cp-theme="light|dark"` on `<html>`. `CpThemeService` manages this attribute, respects system preference, and persists the user's explicit choice. Never create separate component markup for dark mode.

## Content hierarchy

- Display serif: page moments, feature headings, editorial quotes.
- Sans bold: controls, labels, item titles, metrics.
- Muted sans: metadata and supporting copy.
- Uppercase micro-label: category or workflow context only.

## Accessibility baseline

Target WCAG 2.2 AA. Preserve visible focus. Do not communicate status by color alone. Touch targets should be at least 40px. Dialogs require a title and close action. Form errors remain adjacent to their field and use `role="alert"`.
