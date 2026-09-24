# CreatorPantry Angular Design System

A production-minded Angular 22 design-system starter based on the supplied creator-productivity dashboard. It includes a reusable library and a responsive showcase application.

## Run it

Run the whole system from the repository root with `aspire run`. Aspire installs packages, starts the
Angular development server (`web-dev`) on an assigned port, and proxies `/runtime-config.json` to
`CreatorPantry.Web`. `npm start` refuses to run without the port and web-host address Aspire provides.

Build both the library and showcase with `npm run build`; run their tests with `npm test`. The
publishable library is emitted to `dist/creator-pantry-ui`, and the showcase bundle to
`dist/showcase/browser`, which `dotnet publish` copies into `CreatorPantry.Web`.

In a development build, `/design-system` renders every state of every primitive (variants, sizes,
disabled, error, empty, overflow) for visual review. The route only exists when `isDevMode()` is
true, so it is absent from production builds.

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
- `CpStatusPillComponent`: semantic status pill (`neutral | progress | success | warning | error | stale`) with a distinct glyph per tone plus required text — meaning never relies on color alone
- `CpListShellComponent`: domain-neutral table/list frame with heading association, actions/pagination slots, and loading, error, empty, and ready states
- `CpTabsComponent` / `CpTabPanelComponent`: keyboard-accessible tabs (ARIA tabs pattern, roving tabindex, disabled tabs) with lazy-mounted panels
- `CpToolbarComponent`: responsive toolbar with search/filters/actions/overflow slots, loading and disabled states, and roving-tabindex keyboard navigation — which yields the arrow, `Home` and `End` keys to a focused text input, `<select>`, `<textarea>` or contenteditable, so a control in the search slot keeps its own caret and value behaviour. The "More actions" disclosure renders only when `[cpToolbarOverflow]` has content, so a toolbar that uses the first three slots shows no empty overflow button
- `CpEmptyStateComponent`: title/description/icon empty state with a projected actions slot and `first-use`/`no-results` variants
- `CpUploaderComponent`: uploader shell (browse + drag/drop) with per-item queued/uploading/success/error rendering, progress, retry, cancel, and remove — no upload transport of its own
- `CpDiffLegendComponent`: accessible legend for diff/proposal states (`added | removed | changed | moved | unchanged | warning | selected`), plus `cpDiffGlyph(kind)` and `cpDiffLabel(kind)` so a surface that marks up individual changes prints the same glyph and wording the legend beside it explains
- `CpToastRegionComponent`: polite/assertive toast region with severity-based auto-dismiss timing, hover/focus pause, deduplication, and persistent warning/error toasts
- Semantic CSS tokens for color, typography, spacing, radii, elevation, and motion

## Guardrails

- Feature code uses `--cp-*` semantic tokens; never copy literal colors from the showcase.
- Components stay domain-neutral. Recipe-specific data and workflows belong in features.
- Use native semantic elements first. Every icon-only control needs an accessible name.
- Keep all components standalone, signal-based, strictly typed, and `OnPush`.
- Test light, dark, keyboard, 200% zoom, narrow mobile, and reduced-motion behavior before merging.

See `DESIGN-SYSTEM.md` for visual decisions and `CLAUDE.md` for Claude Code instructions.
