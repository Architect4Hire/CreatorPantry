# CreatorPantry — Public Landing Page SCRUB Prompts

Atomic prompts for implementing a public, unauthenticated landing/sign-up page at the application's
default route (`/`), for use with Claude Code and the CreatorPantry `.claude/` toolkit.

This document supplements `docs/prompts/creatorpantry-scrub-microprompts.md`. It is scoped to one
observable outcome: an anonymous visitor to `/` sees an engaging marketing page that leads to sign-up,
and from there to sign-in, instead of being redirected straight past a blank auth wall. It does not
redefine anything already decided in `CLAUDE.md` or `.claude/rules/`.

## Reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

## Prerequisites already satisfied

These already exist and should be reused, not rebuilt:

- `/sign-in`, `/sign-up`, and `/confirm-email` are registered, ungated Angular routes
  (`src/web/src/app/app.routes.ts`), backed by working `SignInComponent`, `SignUpComponent`, and
  `AuthService`/`RegistrationService`.
- `SignUpComponent` already shows a "check your email" confirmation state rather than auto-authenticating,
  so registration already ends by directing the creator toward sign-in — the landing page only needs to
  link to `/sign-up` and `/sign-in`; it does not own that handoff.
- The installed design system (`@creator-pantry/ui`) has `CpButtonComponent`, `CpCardComponent`,
  `CpBadgeComponent`, and the editorial/productivity type and token system described in
  `src/web/DESIGN-SYSTEM.md`.
- `authGuard` (`src/web/src/app/core/auth.guard.ts`) already redirects an unauthenticated visitor at any
  guarded route to `/sign-in`.

## What this phase actually changes

Today, `''` (the app root) is itself the authenticated app shell behind `authGuard` — an anonymous visitor
is bounced straight to a bare `/sign-in` form with no context for what they're signing in to. This phase
inserts a public marketing page at `''` for anonymous visitors and moves the authenticated entry point so a
signed-in visitor still lands in their workspace. This is a routing/access change as much as a content
change, so it starts with a plan, not a component.

## Credentials and external prerequisites

None. This phase needs no new secrets, providers, or infrastructure — it consumes routes and components
that already exist locally.

## Checkpoint

| After | Demonstrable result |
| --- | --- |
| **L.9** | An anonymous visitor to `/` sees the landing page and can reach `/sign-up` and `/sign-in` from it; an authenticated visitor to `/` lands in their workspace, not the marketing page; the page passes the design system's a11y/responsive checklist. |

---

## L.1 Landing route and access decision - done

```text
SCOPE: Decide and document how the public landing route coexists with the authenticated app shell:
landing owns '' for anonymous visitors, an authenticated visitor hitting '' is sent into the app
(workspace gate) instead of seeing marketing content, and /sign-in, /sign-up, /confirm-email stay
ungated top-level routes exactly as they are today.
CONSTRAINT: .claude/rules/frontend.md and auth.md; current src/web/src/app/app.routes.ts and
app.routes.spec.ts, which currently assert authGuard directly on ''.
RESTRICTION: Do NOT weaken guarding on any authenticated section route. Do NOT add a second source of
session truth outside AuthService. The landing route must never request or expose workspace-scoped
data — it renders before any workspace context exists.
BEHAVIOR: Inspect current routing and its existing test, show the redirect/guard plan (e.g. an
anonymous-only check on '' that forwards an authenticated session into the app), wait for approval,
then update only routing and its test in this prompt — no landing UI yet.
```

## L.2 Landing page narrative and information architecture - done

```text
SCOPE: Write the landing page's content plan as a short outline: hero headline/subheadline, primary CTA
("Start free" → /sign-up) and secondary CTA ("Sign in" → /sign-in), 3–4 value-proposition sections
grounded in capabilities that are actually in bounds per CLAUDE.md (recipe development and versioning,
content-project derivatives, AI-assisted drafting as a reviewable proposal, provider-neutral
publishing), an ordered "how it works" walkthrough, and a trust/mission section.
CONSTRAINT: CLAUDE.md Scope section; .claude/rules/ai.md and content.md; the editorial-warmth +
productivity-density voice in src/web/DESIGN-SYSTEM.md.
RESTRICTION: Do NOT describe out-of-bounds capabilities (consumer recipe directory/marketplace,
autonomous publishing, nutrition/medical/food-safety guarantees) as available today. Do NOT invent
adoption numbers, reviews, ratings, or press/brand logos. Every claim must trace to a capability this
codebase actually has or has planned in bounds.
BEHAVIOR: Draft the section-by-section copy outline, wait for approval, then hand off to the component
prompts below. No components are built in this prompt.
```

## L.3 Landing hero section - done

```text
SCOPE: Add a LandingHeroComponent: headline, subheadline, primary/secondary CTA using
CpButtonComponent routed to /sign-up and /sign-in, editorial display typography, responsive layout,
and an optional supporting illustration/screenshot slot.
CONSTRAINT: .claude/rules/design-system.md and frontend.md; creatorpantry-design-system and
new-component skills; the approved copy outline from L.2.
RESTRICTION: No literal brand colors — tokens only. No HttpClient or data fetching in this
presentational component. Must stay legible and usable at 200% zoom and at narrow/mobile widths.
BEHAVIOR: Show the component contract (inputs/outputs, states, ARIA semantics), wait for approval,
implement with component and accessibility tests, report.
```

## L.4 Value-proposition / feature-highlight section - done

```text
SCOPE: Add a LandingValuePropsComponent rendering the capability cards from L.2 as a
CpCardComponent grid, each with a CpBadgeComponent tag and short benefit-led copy.
CONSTRAINT: same as L.3; the approved capability list from L.2.
RESTRICTION: A card may not present a capability as generally available if the underlying feature is
still a placeholder section in the product (per src/web/src/app/app.routes.ts SECTION_ROUTES) — label
those "in the works" rather than overstating or silently omitting them.
BEHAVIOR: Show the card contract and copy-to-capability mapping, wait for approval, implement with
component and accessibility tests, report.
```

## L.5 "How it works" walkthrough section - done

```text
SCOPE: Add a LandingHowItWorksComponent showing the ordered creator journey: create or import a
recipe → request an AI-assisted draft, delivered as a reviewable proposal → approve and version it →
publish through a connected provider or export. Copy must mirror the real lifecycle, not a shortcut
narrative.
CONSTRAINT: .claude/rules/ai.md, publishing.md, and recipes.md — proposal, version, and publication
lifecycles as actually specified there.
RESTRICTION: Do NOT depict AI output as auto-applied to a recipe or content ever, and do NOT depict
publishing as automatic — every step touching AI or publishing must reflect the proposal/confirmation
gate that already governs those domains.
BEHAVIOR: Show the step sequence and its mapping to the real domain lifecycle, wait for approval,
implement with component and accessibility tests, report.
```

## L.6 Trust and mission section - done

```text
SCOPE: Add a LandingTrustComponent communicating credibility honestly: a mission statement, who the
product is for (food bloggers and content creators), and current-stage framing supplied by the
requester rather than invented.
CONSTRAINT: the tone in src/web/DESIGN-SYSTEM.md.
RESTRICTION: No fabricated testimonials, press/brand logos, star ratings, or customer counts. If no
real proof points exist yet, use mission/vision copy instead and flag that real testimonial content is
a follow-up item rather than inventing one.
BEHAVIOR: Propose the section's actual copy for approval (flagging any claim you cannot source from the
requester or the codebase), wait for approval, implement with component and accessibility tests,
report.
```

## L.7 Final CTA band and footer - done

```text
SCOPE: Add a LandingFinalCtaComponent (repeats the primary/secondary CTA) and a minimal footer for the
landing page: product name, current year, a sign-in link, and placeholder legal links clearly marked as
placeholders.
CONSTRAINT: .claude/rules/design-system.md and frontend.md.
RESTRICTION: Placeholder legal links must be visibly placeholders (e.g. disabled or labeled "coming
soon"), never fabricated policy text presented as real.
BEHAVIOR: Show the section contract, wait for approval, implement with component and accessibility
tests, report.
```

## L.8 Landing page composition and route wiring - done

```text
SCOPE: Compose a LandingComponent from L.3–L.7, wire it as the public '' route per the L.1 decision,
update the authenticated redirect target so a signed-in visitor to '' still lands in the app shell, and
update app.routes.spec.ts to assert the new anonymous/authenticated behavior at '' instead of the old
direct-authGuard assertion.
CONSTRAINT: the L.1 decision; .claude/rules/frontend.md; existing app.routes.ts and app.routes.spec.ts.
RESTRICTION: Do NOT remove or weaken authGuard on any authenticated section route. The landing page only
links to the existing /sign-in and /sign-up routes — it does not duplicate session or registration
logic.
BEHAVIOR: Show the final route table for both anonymous and authenticated visitors, wait for approval,
implement, update the routing test, and report the before/after behavior at '/'.
```

## L.9 Landing page verification - done

```text
SCOPE: Verify that an anonymous visitor at '/' sees the landing page and can reach /sign-up and
/sign-in from its CTAs, that an authenticated visitor at '/' lands in the app instead of the marketing
page, and that the page meets the design system's accessibility and responsive checklist (WCAG 2.2 AA,
keyboard operation, visible focus, 200% zoom, narrow layout, reduced motion, both themes).
CONSTRAINT: the verification checklist in .claude/rules/design-system.md and the Verification section
of .claude/rules/frontend.md.
UTILIZATION: design-review agent (read-only) against the new landing components and routes.
RESTRICTION: Assert behavior through real routing and guards, not by bypassing them with direct
component instantiation.
BEHAVIOR: Run design-review, fix only findings within this phase's scope, run npm build and npm test
from src/web/, and report proof for each behavior listed above.
```
