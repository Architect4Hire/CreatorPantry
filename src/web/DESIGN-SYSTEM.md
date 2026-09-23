# CreatorPantry visual system

## Direction

CreatorPantry is a focused production workspace for food bloggers and content creators—not a recipe index. The interface pairs the brand's warm, hand-in-the-kitchen energy with productivity-tool density: bold and rounded where the [logo](public/images/logo.png) is bold and rounded, vivid where a pantry of fresh produce is vivid, never flattened into a muted corporate palette. Fredoka gives content moments the same friendly, chunky display voice as the wordmark; DM Sans keeps controls and data highly legible. Evergreen (drawn from the mixing bowl in the mark) is the action color. Pink, orange, purple, and blue—each saturated enough to read as fresh tomato, carrot, eggplant, and blueberry rather than a desaturated tint—distinguish tools and states without competing with primary actions.

## Layers

1. Primitive tokens define scale: spacing, type, radii, motion, z-index (`--cp-z-topbar` < `--cp-z-sidebar` < `--cp-z-overlay` < `--cp-z-modal`).
2. Semantic theme tokens define intent: background, surface, text, border, primary, status, `--cp-ink-on-accent` (readable text/icon color for any solid accent background), `--cp-scrim` (modal backdrop).
3. Angular primitives implement interaction and accessibility.
4. Feature compositions combine primitives for recipe editing, AI writing, image production, planning, and analytics.

## Theme contract

Set `data-cp-theme="light|dark"` on `<html>`. `CpThemeService` manages this attribute, respects system preference, and persists the user's explicit choice. Never create separate component markup for dark mode.

## Content hierarchy

- Display rounded sans (Fredoka): page moments, feature headings, editorial quotes.
- Sans bold: controls, labels, item titles, metrics.
- Muted sans: metadata and supporting copy.
- Uppercase micro-label: category or workflow context only.

## Accessibility baseline

Target WCAG 2.2 AA. Preserve visible focus. Do not communicate status by color alone. Touch targets should be at least 40px. Dialogs require a title and close action. Form errors remain adjacent to their field and use `role="alert"`. `global.css` honors `prefers-reduced-motion: reduce` for every animation/transition; do not add motion that bypasses it.
