# CreatorPantry Angular Design System

A production-minded Angular 22 design-system starter based on the supplied creator-productivity dashboard. It includes a reusable library and a responsive showcase application.

## Run it

Run the whole system from the repository root with `aspire run`. Aspire installs packages, starts the
Angular development server (`web-dev`) on an assigned port, and proxies `/runtime-config.json` to
`CreatorPantry.Web`. `npm start` refuses to run without the port and web-host address Aspire provides.

Build both the library and showcase with `npm run build`; run their tests with `npm test`. The
publishable library is emitted to `dist/creator-pantry-ui`, and the showcase bundle to
`dist/showcase/browser`, which `dotnet publish` copies into `CreatorPantry.Web`.

## Use the library

Import the three CSS foundations once in the consuming application's global styles, in this order:

```css
@import '@creator-pantry/ui/styles/tokens.css';
@import '@creator-pantry/ui/styles/themes.css';
@import '@creator-pantry/ui/styles/global.css';
```

Then import only the standalone components a feature needs:

```ts
import { CpButtonComponent, CpCardComponent } from '@creator-pantry/ui';
```

```html
<cp-card>
  <h2>Recipe draft</h2>
  <button cpButton>Continue writing</button>
</cp-card>
```

## Included API

- `CpThemeService`: persisted light/dark/system theme preference
- `CpButtonComponent`: primary, secondary, ghost, text, and danger variants
- `CpCardComponent`: standard, interactive, and flush surfaces
- `CpBadgeComponent`: semantic status and category tones
- `CpFieldComponent`: accessible form label, hint, required, and error treatment
- `CpProgressComponent`: bounded accessible progress indicator
- `CpDialogComponent`: modal shell with backdrop dismissal and focus target
- `CpQuickActionComponent`: branded creator-tool action card
- Semantic CSS tokens for color, typography, spacing, radii, elevation, and motion

## Guardrails

- Feature code uses `--cp-*` semantic tokens; never copy literal colors from the showcase.
- Components stay domain-neutral. Recipe-specific data and workflows belong in features.
- Use native semantic elements first. Every icon-only control needs an accessible name.
- Keep all components standalone, signal-based, strictly typed, and `OnPush`.
- Test light, dark, keyboard, 200% zoom, narrow mobile, and reduced-motion behavior before merging.

See `DESIGN-SYSTEM.md` for visual decisions and `CLAUDE.md` for Claude Code instructions.
