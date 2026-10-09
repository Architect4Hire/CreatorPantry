# AI Fluency Decisions

*Status: Accepted — 2026-10-09. Recorded by AI-fluency prompt AF.1.1.*

This record fixes the product decisions behind the AI-fluency work — turning AI Recipe Studio, Image Studio
and the Content Pipeline into one connected subsystem — so the AF prompts build on a stated baseline instead
of an assumption. It does not implement code, and it records nothing the AF prompt document does not state.

Identifiers continue the `B-` sequence from [`baseline.md`](baseline.md), which ends at B-26. Do not renumber
them; later documents may reference them.

## Sources

- `docs/prompts/creatorpantry-ai-fluency-scrub-prompts.md` — the AF prompts, the feedback table, and the
  decisions recorded here.
- Creator feedback received 2026-10-09, tracked as FLU-001 through FLU-009 in that document.
- Product-owner answers given 2026-10-09 — the four decisions below.
- `docs/prompts/creatorpantry-scrub-microprompts.md` — the master library whose prompts are superseded or
  narrowed.
- `CLAUDE.md` and `.claude/rules/` — architectural source of truth. Nothing here redefines them.

## Decision table

| ID | Topic | Feedback | Status | Decision |
| --- | --- | --- | --- | --- |
| B-27 | Posts for creator-selected channels | FLU-001 | Decided; one assumption **to confirm** | Posts are written for the channels the creator picks from `ContentChannelCatalog`, not a fixed set of seven. |
| B-28 | Image compression | FLU-007 | Decided; method **open until AF.5.1** | Server-side, with no external NuGet image package if at all possible. |
| B-29 | Themed-day memory | FLU-009 | Decided | Creator-written standing notes plus derived history. Nothing is learned or written by a model. |
| B-30 | Scope: connected flows | FLU-001 – FLU-009 | Decided | Every feedback item is fixed and work can be handed from any AI surface to any other. No chat assistant. |
| B-31 | `CreativeContext` | FLU-003, 004, 006, 008, 009 | Concept recorded; entity design is AF.1.2's | One server-side record of what a piece of creative work is about, holding references by id. |

## Decisions

### B-27 Posts for creator-selected channels

- **Posts are written for the channels the creator picks**, not a fixed set of seven outputs.
- **The vocabulary is the existing `ContentChannelCatalog`.** No second channel list is introduced.
- **For the owned channels the output is short-form:** `blog` yields a blog intro and `newsletter` a
  newsletter blurb.
- **The long-form article stays with `content.editorial-package`** (master 11.4). Long-form blog articles and
  SEO packages are out of bounds for the AF work.

**To confirm:** the short-form reading of `blog` and `newsletter` is an assumption drawn from the product
owner's answer, not a statement of it. It is confirmed in the AF.6.2 plan; until then treat it as a working
direction. See [Open items](#open-items).
**Consequences:** the seven-output social model and generation prompts in the master library are superseded —
see [Supersessions](#supersessions).
**Rules:** `content.md`, `ai.md`.

### B-28 Image compression

- **Compression is server-side.**
- **No external NuGet package, if at all possible.** .NET has no built-in cross-platform image codec, so this
  means a small managed codec owned by the Media module, plus asking the image provider for a smaller format
  where it supports one.
- **AF.5.1 proves that out before anything is built on it**, and adds its findings to this record as a section
  of its own: what the provider can return, the managed path, the rendition set, and the input formats in
  scope.

**Options considered:** `System.Drawing` was rejected — it is Windows-only and is not an option for a
containerised Worker.
**Consequences:** if the AF.5.1 spike shows the managed path is not viable, the work stops and reports with
the numbers rather than adding a dependency. See [Open items](#open-items).
**Rules:** `media.md`, `external.md`.

### B-29 Themed-day memory

- **Day memory is creator-written notes plus history.**
- **Each themed day carries standing instructions the creator writes.**
- **Generation is shown what was already made for that day** so it does not repeat itself.
- **Nothing is learned or written by a model.** Any model-written memory is out of bounds for the AF work.

**Rules:** `ai.md`, `tenancy.md`.

### B-30 Scope: connected flows

- **Every feedback item (FLU-001 through FLU-009) is fixed.**
- **A shared creative context lets a concept, recipe, image, prompt, or themed day be handed from any AI
  surface to any other** — see [B-31](#b-31-creativecontext).
- **No chat assistant.** A conversational assistant is out of bounds for the AF work.

**Consequences:** also out of bounds for the AF prompts — scheduling, the content board and publishing (master
Phase 13), and an allowance charge for image generation, which 12.10l left open and which still needs a prompt
of its own.
**Rules:** `ai.md`, `content.md`.

### B-31 CreativeContext

*The concept as AF.1.2 states it. The entity, its indexes, delete behaviours and migration are designed and
approved in AF.1.2; this entry fixes only what that prompt already commits to.*

- **`CreativeContext` is the server-side record of what one piece of creative work is about.** It is
  workspace-owned and lives in the Content module. It answers the two structural gaps under the feedback:
  Content Pipeline and Image Studio state lived only in the browser, and there was no shared record each
  surface could read instead of re-asking for the recipe, the channel, the day and the picture.
- **It holds the creator's own words** — a working title and the picture they have in mind — the chosen
  channel keys, an optional day of week and weekly-theme key, and an ordered set of `CreativeContextReference`
  rows.
- **Each reference names one source by kind and id:** recipe (plus pinned version), recipe concept (request id
  plus concept id), DAM asset (plus version), staged generated image, prompt record, social package.
- **References are ids, never copies.** A context does not snapshot recipe text, image bytes or prompt bodies,
  and never becomes a competing source of truth.
- **No step or progress state.** That belongs to 13A's `WorkflowRun`, which links to a context by id. Neither
  replaces the other.
- **A reference whose target is later deleted or archived must not break the context.**
- **No provider-specific field.** Foreign keys that cross modules name entity types only; everything else
  crosses facade to facade.
- **It carries a row version for concurrency, created-by, and archived-at.**

**Consequences:** generation tasks read a context through a deterministic, bounded grounding package (AF.1.5),
mirroring `BrandContextAssembler` and the `BrandContextPackage` ([B-24](baseline.md)).
**Rules:** `tenancy.md`, `backend.md`, `content.md`, `ai.md`.

## Supersessions

The effect of the AF prompt document on master-library prompts that had not been run.

| Master prompt | Effect |
| --- | --- |
| 13.1 Social package model | **Superseded by AF.6.1** — creator-selected channels replace seven fixed outputs. |
| 13.2 Seven-output social generation | **Superseded by AF.6.3–AF.6.4.** |
| 13.3a Content Pipeline social step | **Superseded by AF.6.5.** |
| 13.7a Content Pipeline keeper commit | **Narrowed.** AF.4.3 delivers the DAM save and prompt lineage; the board card remains with 13.7a. |
| 9.4a / 9.4b first-draft review and create | Server side is built. AF.2.1–AF.2.2 deliver the missing web half. |
| 13.3 Social Studio UI | Unchanged, but builds on AF.6 rather than 13.1–13.2. |
| 13A.2 Workflow run entities | Unchanged. A `WorkflowRun` links to a `CreativeContext` by id; neither replaces the other. |
| 13A.19 "Create food images" workflow | Unchanged; it composes the pieces delivered by the AF prompts. |

13.1, 13.2, 13.3a and 13.7a each carry a one-line pointer in the master file. The superseded prompts are kept
there, unedited, for the record.

## Open items

### B-27 Short-form reading of `blog` and `newsletter` — CONFIRM

- **Question:** does picking `blog` or `newsletter` produce a short-form piece (an intro, a blurb), as assumed?
- **Owner:** the AF.6.2 plan.
- **Default assumed by later prompts:** short-form, with the long-form article left to master 11.4.

### B-28 How compression is done — DECIDE

- **Question:** can a small managed codec, with no external package, meet FLU-007?
- **Owner:** AF.5.1, which adds its section to this record. No later Phase 5 prompt runs before it is approved.
- **Default assumed by later prompts:** none. If the managed path is not viable, stop and report.
