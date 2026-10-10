# CreatorPantry — AI Fluency SCRUB Prompts

Atomic prompts for turning CreatorPantry's separate AI surfaces — AI Recipe Studio, Image Studio, and the
Content Pipeline — into one connected subsystem, for use with Claude Code and the CreatorPantry `.claude/`
toolkit.

This document supplements `docs/prompts/creatorpantry-scrub-microprompts.md` (the "master library"). It does
not redefine anything decided in `CLAUDE.md` or `.claude/rules/`. Where it overlaps a master prompt that has
not been run yet, the overlap is listed under [Relationship to the master library](#relationship-to-the-master-library)
and AF.1.1 records it in the master file.

## Reusable SCRUB skeleton

```text
SCOPE:        one observable change and the exact repository seam it touches
CONSTRAINT:   stack, rule files, skills, requirement IDs, and prerequisites
RESTRICTION:  explicit exclusions and invariants that must not be weakened
UTILIZATION:  skills, read-only reviewers, and tools to invoke
BEHAVIOR:     inspect, plan, wait for approval, implement, test, and report
```

Run one prompt at a time, in order, in a clean context where practical. Mark a prompt `- done` only after
its stated verification passes, and add a delivery note under it when what shipped differs from what was
asked.

## The feedback this answers

Creator feedback received 2026-10-09, with the requirement ID each item is tracked under here:

| ID | Feedback | Phase |
| --- | --- | --- |
| FLU-001 | Content Pipeline does not write the posts. | 6 |
| FLU-002 | Content Pipeline images cannot be saved to the DAM. | 4 |
| FLU-003 | Content Pipeline cannot use an existing image as inspiration or as input to content generation. | 3, 6 |
| FLU-004 | The photo described in pipeline step 1 cannot be used in step 2. | 3 |
| FLU-005 | Image Studio output cannot be saved to the DAM. | 4 |
| FLU-006 | Image Studio cannot link or import a recipe to build the prompt. | 3 |
| FLU-007 | Images from both surfaces need to be compressed so they are smaller. | 5 |
| FLU-008 | AI Recipe Studio is a dead end: a chosen concept cannot feed recipe generation, image generation, or the pipeline. | 2 |
| FLU-009 | No way to define themed days of the week and keep memory for those days that generation uses. | 7 |

## Decisions already made

These were answered by the product owner on 2026-10-09. Do not reopen them inside a prompt; if one turns out
to be unworkable, stop and report.

1. **Posts are written for the channels the creator picks**, not a fixed set of seven. The vocabulary is the
   existing `ContentChannelCatalog`. For the owned channels the output is short-form: `blog` yields a blog
   intro and `newsletter` a newsletter blurb. The long-form article stays with `content.editorial-package`
   (master 11.4). *The short-form reading of `blog`/`newsletter` is an assumption drawn from that answer —
   confirm it in the AF.6.2 plan.*
2. **Compression is server-side with no external NuGet package if at all possible.** .NET has no built-in
   cross-platform image codec (`System.Drawing` is Windows-only and is not an option for a containerised
   Worker), so this means a small managed codec owned by the Media module, plus asking the image provider for
   a smaller format where it supports one. AF.5.1 proves that out before anything is built on it.
3. **Day memory is creator-written notes plus history.** Each themed day carries standing instructions the
   creator writes, and generation is shown what was already made for that day so it does not repeat itself.
   Nothing is learned or written by a model.
4. **Scope is connected flows.** Every feedback item is fixed, and a shared creative context lets a concept,
   recipe, image, prompt, or themed day be handed from any AI surface to any other. No chat assistant.

## How this list was built

A code map taken on 2026-10-09 (branch `dev`, after `ced1085 prompts upto 12.10L`). File references below
come from that map re-find them before relying on a line number.

The honest finding: **most of this feedback is a missing hand-off, not missing AI.** The backend is ahead of
the web app in almost every flow.

| Feedback | What exists | Where it stops |
| --- | --- | --- |
| FLU-001 | Nothing. No post-writing capability, no `SocialPackage`; `social-studio` is a placeholder route. | The pipeline's `posts` step renders `content-pipeline-step-placeholder.component.ts`. |
| FLU-002, FLU-005 | `POST /dam-assets/from-generated-image` → `MediaAssetBusiness.CreateFromGeneratedImageAsync` creates an `AiGenerated` asset, flips the staged image to `Kept`, and can take metadata, a recipe link, and a prompt record in one transaction. Covered by `CreativePipelineEndToEndTests`. | No Angular caller. `dam-asset.service.ts` has no create method. "Keepers" are a `string[]` in `localStorage`. |
| FLU-003 | `reference-image-requests` analyses a picture; `dam-asset-picker.component.ts` exists. | `ReferenceDocumentId` only accepts a Brand Library source document. A DAM asset or an earlier generated image cannot be chosen. |
| FLU-004 | Setup collects "the picture you have in mind" (`config.concept`). | The idea step's seed is deterministic and never receives it; it reaches the prompt step only as a fallback inside the concept panel. |
| FLU-006 | `AiPhotographyConceptRequestModels` and `AiImagePromptRequestModels` accept `RecipeId`/`RecipeVersionId`. | The web models never send them, and Image Studio has no recipe picker. |
| FLU-007 | `GeneratedImageInspector` sniffs signatures and dimensions. | No codec anywhere. Venice returns PNG at the model's default size and the bytes are stored untouched. `MediaAssetKind.Derived` is never assigned. |
| FLU-008 | `POST recipe-draft-requests` takes `SourceConceptRequestId` + `SourceConceptId`; `POST {requestId}/acceptance` creates the recipe. | `recipe-concept-studio.component.ts` `choose()` emits `conceptSelected`, which nothing consumes. `recipe-draft.service.ts` can only watch status. |
| FLU-009 | `WorkspaceWeeklyTheme` (one per day) with `GET/PUT/DELETE weekly-themes`. | No Angular service or screen. Only the theme's display name reaches generation, through the seed description. No notes, no history. |

Two structural gaps sit under all of it and are fixed first:

- **Pipeline and Image Studio state lives only in the browser** (`ContentPipelineDraft`, `ImageStudioDraftService`,
  both `localStorage`). The master library's own rule is that a multi-step journey does not keep private
  content only in browser memory, and nothing server-side can be handed from one surface to another.
- **There is no shared record of "what this piece of work is about."** Each surface re-asks for the recipe,
  the channel, the day, and the picture.

## Prerequisites already satisfied

Reuse these; do not rebuild them:

- The AI operation, proposal, disposition, and worker lifecycle (`Modules/Ai`), the versioned prompt loader
  (`Managers/Prompts/`), `AiTaskCatalog`, the untrusted-context envelope, and the evaluation harness under
  `src/CreatorPantry.Tests/Ai/Evaluation/`.
- `BrandContextAssembler` and the `BrandContextPackage` — the pattern AF.1.5 mirrors.
- The staged-image lifecycle: `GeneratedImage` (`Staged | Kept | Rejected | Expired`),
  `GeneratedImageWorkerHostedService`, retention sweep, preview/content routes.
- The DAM: `MediaAsset`, `MediaAssetVersion`, tags, utilization, recipe asset links, `PromptRecord`.
- `ContentChannelCatalog` and `IWorkspaceWeeklyThemeFacade`.
- Shared web compositions: `cp-generated-image-run`, the concept/prompt/reference panels, `dam-asset-picker`,
  `DamLinkedAssetComponent`, and `ConfirmService` for confirmations.

## Relationship to the master library

| Master prompt | Effect of this document |
| --- | --- |
| 13.1 Social package model | **Superseded by AF.6.1** — creator-selected channels replace seven fixed outputs. |
| 13.2 Seven-output social generation | **Superseded by AF.6.3–AF.6.4.** |
| 13.3a Content Pipeline social step | **Superseded by AF.6.5.** |
| 13.7a Content Pipeline keeper commit | **Narrowed.** AF.4.3 delivers the DAM save and prompt lineage; the board card remains with 13.7a. |
| 9.4a / 9.4b first-draft review and create | Server side is built. **AF.2.1–AF.2.2 deliver the missing web half.** |
| 13.3 Social Studio UI | Unchanged, but builds on AF.6 rather than 13.1–13.2. |
| 13A.2 Workflow run entities | Unchanged. A `WorkflowRun` links to a `CreativeContext` by id; neither replaces the other. |
| 13A.19 "Create food images" workflow | Unchanged; it composes the pieces delivered here. |

## Out of bounds for this document

- Scheduling, the content board, and publishing (master Phase 13).
- An allowance charge for image generation. 12.10l left it open because it needs pricing decisions; it still
  needs a prompt of its own.
- A conversational assistant or any model-written memory.
- Long-form blog articles and SEO packages (master 11.4–11.5).

## Credentials and external prerequisites

Chat and embeddings deployments (Foundry) and `venice-api-key`, as for master Phase 12. Evaluation fixtures
run without a live provider.

## Checkpoints

| After | Demonstrable result |
| --- | --- |
| **AF.1.5** | A creative context can be started from a recipe, concept, image, or themed day, survives a refresh on another device, and resolves into a bounded grounding package that never crosses a workspace. |
| **AF.2.3** | A creator chooses a concept in AI Recipe Studio and, without retyping anything, gets a recipe draft, a picture, or a pipeline run from it. |
| **AF.3.5** | Image Studio and the pipeline can start from a recipe and from an existing picture, and the photo described in setup is visibly what the next step works from. |
| **AF.4.3** | A generated picture from either surface is in the DAM with its prompt, alt text, and optional recipe link, and saving twice makes one asset. |
| **AF.5.6** | Every generated picture is served at a fraction of its stored size, with a thumbnail, with no third-party image package and the provider original intact. |
| **AF.6.6** | The pipeline writes posts for the channels the creator picked, each reviewed and accepted independently, with limits enforced by code. |
| **AF.7.4** | A creator defines themed days with standing notes, and generation for that day follows the notes and avoids what was already made. |
| **AF.8.2** | One recorded journey — concept → recipe → picture → DAM → posts on a themed day — passes end to end over HTTP for two workspaces that cannot see each other. |

---

# Phase 1 — Decisions and the shared creative context

## AF.1.1 Decision record and master-library reconciliation

```text
SCOPE: Documentation only. Add docs/architecture-decisions/ai-fluency.md recording the four product
decisions above, the CreativeContext concept (AF.1.2), and the supersessions in "Relationship to the master
library". In docs/prompts/creatorpantry-scrub-microprompts.md, annotate 13.1, 13.2, 13.3a and 13.7a with a
one-line pointer to the AF prompt that supersedes or narrows each, and move the "pick up here" marker.
CONSTRAINT: docs/architecture-decisions/baseline.md for the record format and decision numbering;
.claude/rules/content.md and ai.md.
RESTRICTION: Do not delete or rewrite the superseded master prompts — annotate them. Do not record a
decision this document does not state. No code.
BEHAVIOR: Show the record outline and the four master-file edits, wait for approval, write them, and report
anything in the master library that conflicts with this document and is not listed in the table.
```

## AF.1.2 CreativeContext entity and migration

```text
SCOPE: Add the workspace-owned CreativeContext — the server-side record of what one piece of creative work
is about — and its typed references, in the Content module. It holds the creator's own words (working
title, the picture they have in mind), the chosen channel keys, optional day of week and weekly-theme key,
and an ordered set of CreativeContextReference rows, each naming one source by kind and id: recipe (+ pinned
version), recipe-concept (request id + concept id), DAM asset (+ version), staged generated image, prompt
record, social package. Row version for concurrency; created-by; archived-at.
CONSTRAINT: FLU-003, FLU-004, FLU-006, FLU-008, FLU-009; .claude/rules/tenancy.md, backend.md, content.md;
add-workspace-entity and add-content-feature skills. Memory: check a new migration against a SQL Server
fixture for multiple cascade paths, and keep CHECK constraints portable.
RESTRICTION: References are ids, never copies — a context does not snapshot recipe text, image bytes, or
prompt bodies, and never becomes a competing source of truth. No step or progress state: that belongs to
13A's WorkflowRun, which will link here by id. No provider-specific field. Foreign keys that cross modules
name entity types only; everything else crosses facade to facade. A reference whose target is later deleted
or archived must not break the context.
UTILIZATION: add-workspace-entity; workspace-isolation-auditor after implementation.
BEHAVIOR: Show the entity, reference kinds, indexes, delete behaviours and what a dangling reference reads
as; wait for approval; implement configuration and migration; review and apply it; test uniqueness, dangling
references, concurrency, and two-workspace isolation.
```

## AF.1.3 CreativeContext endpoints

```text
SCOPE: Add the request seam for a creative context under
/api/v1/workspaces/{workspaceSlug}/creative-contexts: create (optionally "from" one source reference, so a
hand-off is one call), read, PATCH the creator's words/channels/day/theme, add a reference, remove a
reference, and list recent contexts by cursor.
CONSTRAINT: AF.1.2; .claude/rules/api-contract.md and backend.md; add-endpoint skill. Create accepts an
Idempotency-Key. Updates use the row version / ETag.
RESTRICTION: Every reference is validated through the owning module's facade in the resolved workspace
before it is stored; unknown and other-workspace targets answer the same 422 with a stable code and disclose
nothing. Channel keys validate through the channel facade; the theme key through
IWorkspaceWeeklyThemeFacade. WorkspaceId never comes from the body. Contributor and above may write; Viewer
may read.
UTILIZATION: add-endpoint; api-contract-checker and workspace-isolation-auditor after implementation.
BEHAVIOR: Show routes, ViewModels, ServiceModels and error codes; wait for approval; implement the full seam
with tests for each reference kind, foreign-workspace references, idempotent create, stale ETag, and
two-workspace isolation; regenerate the OpenAPI snapshot and read the diff.
```

## AF.1.4 CreativeContext web service and hand-off control

```text
SCOPE: Add the typed web side: models in src/web/src/app/models/, a CreativeContextService in
src/web/src/app/services/, and one shared composition in src/web/src/app/shared/ — a "Use this in…" control
that, given a source reference, creates a context from it and navigates to a chosen destination (recipe
draft, Image Studio, Content Pipeline) carrying the context id in the route.
CONSTRAINT: AF.1.3; .claude/rules/frontend.md and design-system.md; new-component and
creatorpantry-design-system skills.
RESTRICTION: Components do not inject HttpClient. The control is a composition of @creator-pantry/ui
exports, not a new primitive, and offers only the destinations it is given. A failed create leaves the
creator where they were with their work intact. No destination is wired in this prompt — AF.2.3 and Phase 3
do that.
UTILIZATION: new-component; design-review after implementation.
BEHAVIOR: Show the models, the control's inputs/outputs and its loading/error states; wait for approval;
implement with service and component specs covering keyboard operation and the failure path.
```

## AF.1.5 Deterministic CreativeContextPackage assembler

```text
SCOPE: Add the server-side assembler that turns a CreativeContext into a bounded grounding package for a
generation task: the creator's words, resolved channel profiles, the day's theme, and a summary of each
reference the task is allowed to use — recipe facts from the pinned version, the chosen concept, a picture's
stored analysis and alt text, a prompt record's text. Exposed through a Content facade and consumed by Ai
task handlers.
CONSTRAINT: AF.1.2; .claude/rules/ai.md (Grounding) and tenancy.md; the BrandContextAssembler and
BrandContextPackage as the pattern to mirror; add-ai-capability skill.
RESTRICTION: Deterministic — no model call inside the assembler. Every reference is re-read through its
owning facade in the caller's workspace at assembly time; one that no longer resolves is dropped and
reported in the package, never guessed. Each section has a documented size cap and truncation rule.
Everything creator-written or retrieved is delimited as untrusted content and cannot redefine tool
permissions. A picture contributes only what a stored analysis or creator-written alt text says about it;
with neither, it contributes "a picture is attached, not described". The package records exactly which
record versions it used.
UTILIZATION: add-ai-capability; ai-safety-reviewer and workspace-isolation-auditor after implementation.
BEHAVIOR: Show the package shape, caps, and the per-task allow-list of sections; wait for approval;
implement with tests for determinism (same inputs, same bytes), truncation, dropped references, an
injection string in each creator-written field, and a two-workspace test proving a foreign reference
contributes nothing.
```

---

# Phase 2 — AI Recipe Studio stops being a dead end

## AF.2.1 Request a recipe draft from a chosen concept

```text
SCOPE: In AI Recipe Studio, make choosing a concept offer "Draft this recipe": extend
recipe-draft.service.ts with the create call for POST recipe-draft-requests (SourceConceptRequestId +
SourceConceptId), and wire recipe-concept-studio.component.ts so the action requests the draft and
navigates to the existing /draft review route.
CONSTRAINT: FLU-008; master 9.4 (built server-side); src/web/src/app/features/ai/;
.claude/rules/frontend.md; new-component skill.
RESTRICTION: Web only — do not change the endpoint. Remove the "Turning a concept into a recipe draft isn't
built yet" copy once it is untrue. The brief the creator filled in survives a failed or refused request,
including an allowance refusal. The idempotency key names the request, so a different concept takes a new
key.
BEHAVIOR: Inspect the endpoint contract and the existing watchStatus flow, show the state transitions, wait
for approval, implement with service and component specs for success, refusal, failure, and retry.
```

## AF.2.2 Accept a reviewed draft into a recipe

```text
SCOPE: On the draft review page, add the acceptance action: call POST recipe-draft-requests/{requestId}/
acceptance, then open the created recipe in the recipe editor.
CONSTRAINT: FLU-008; master 9.4b (built server-side: AiDraftAcceptanceBusiness.AcceptAsync);
.claude/rules/recipes.md and ai.md.
RESTRICTION: The draft is visibly generated and remains reviewable before acceptance; acceptance is an
explicit action, confirmed through ConfirmService. Retrying an acceptance must not create a second recipe —
use the route's idempotency behaviour, do not invent one client-side. A rejected or expired draft offers no
accept action.
BEHAVIOR: Inspect the acceptance contract, show the states, wait for approval, implement with specs for
accept, double-submit, expired draft, and failure.
```

## AF.2.3 Hand a concept to Image Studio and the Content Pipeline

```text
SCOPE: Add the AF.1.4 hand-off control to a chosen concept in AI Recipe Studio, with three destinations:
draft the recipe (AF.2.1), make a picture (Image Studio), and start a content run (Content Pipeline). The
last two create a CreativeContext from the recipe-concept reference and open the destination with its id.
CONSTRAINT: FLU-008; AF.1.4. Image Studio and the pipeline read the context in Phase 3 — until then the
destinations open with the context id and ignore it, and this prompt's test asserts the id arrives.
RESTRICTION: One obvious primary action (draft the recipe); the others are secondary. Delete the unused
conceptSelected output if nothing consumes it after this change.
BEHAVIOR: Show the action hierarchy, wait for approval, implement with component specs and a routing test.
```

---

# Phase 3 — Images that start from a recipe, a picture, or the creator's own description

## AF.3.1 Content Pipeline and Image Studio state moves onto the creative context

```text
SCOPE: Make both surfaces read and write a CreativeContext instead of keeping private content only in
localStorage. Channel, day, theme, the picture the creator has in mind, and every source reference live on
the context; ContentPipelineDraftService and ImageStudioDraftService keep only per-device conveniences
(current step, panel state, unsent text) keyed by context id.
CONSTRAINT: FLU-004; AF.1.3–AF.1.4; src/web/src/app/features/content-pipeline/ and image-studio/;
.claude/rules/frontend.md.
RESTRICTION: An existing local draft is migrated into a new context once, then cleared — a creator
mid-run does not lose work. Autosave is debounced and uses the ETag; a conflict keeps the creator's edit and
offers a reload. A context id in the route that does not resolve reads as not found, not as an empty run.
Staged-image ids and keepers are not part of this prompt.
BEHAVIOR: Show the field-by-field mapping from draft v4 to context, the migration, and the conflict state;
wait for approval; implement with specs for migration, resume on a fresh browser profile, conflict, and
offline failure.
```

## AF.3.2 The described photo carries from setup into every later step

```text
SCOPE: Fix FLU-004. The picture described in setup is shown at the idea step beside the generated seed with
an explicit choice — work from my description, work from this idea, or combine them — and the chosen brief
is displayed, editable, at the head of the prompt step. The concept and prompt requests send the chosen
brief; no step reads it through a fallback.
CONSTRAINT: AF.3.1; content-pipeline-setup-step, the idea step, and content-pipeline-concept-panel
(today: config.concept, falling back to the seed description, sent as creatorConcept).
RESTRICTION: The seed generator stays deterministic and is not given free text. The creator's description
is never rewritten — "combine" appends the seed below it, visibly. The choice is stored on the context so a
resumed run shows the same brief.
BEHAVIOR: Show the three-choice state and what each sends, wait for approval, implement with specs proving
the described photo reaches the photography-concept request in each mode.
```

## AF.3.3 Link a recipe in Image Studio and the Content Pipeline

```text
SCOPE: Fix FLU-006. Add a recipe picker (search the library, choose a recipe, pin its current version) to
Image Studio and to pipeline setup. The choice is stored as a recipe reference on the context, shown as a
removable summary, and sent as RecipeId/RecipeVersionId on photography-concept and image-prompt requests.
CONSTRAINT: AF.3.1; AiPhotographyConceptRequestModels and AiImagePromptRequestModels already accept both
ids; the recipe list service; new-component and creatorpantry-design-system skills.
RESTRICTION: Web and context only — no backend change unless the plan finds one. The version is pinned at
selection and a newer version is shown as "this recipe has changed" with a re-pin action, never switched
silently. Archived recipes are not offered. If a second surface needs the same picker, build it once in
src/app/shared/.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the picker's states (loading, empty, no results, error) and the request change, wait for
approval, implement with specs proving both ids are sent and that an unlinked run sends neither.
```

## AF.3.4 Analyse a DAM asset or a generated image as a reference

```text
SCOPE: Fix the backend half of FLU-003. Extend reference-image-requests so the picture to analyse may be a
DAM asset version or a staged or kept generated image, in addition to a brand source document — one
explicit source discriminator, exactly one source per request.
CONSTRAINT: AiReferenceImageRequestModels (today: ReferenceDocumentId only); image.reference-analysis;
.claude/rules/media.md, ai.md and api-contract.md; add-ai-capability and add-endpoint skills. Renditions
from Phase 5 are preferred as model input when present.
RESTRICTION: Additive contract — ReferenceDocumentId keeps working. The Ai module reaches the picture
through the Media facade, which authorises metadata before bytes; a foreign, deleted, declined, or expired
picture answers the same 422. The analysis describes visible content only and is stored so AF.1.5 can reuse
it without a second model call. If the prompt template's behaviour changes, bump its version and update the
evaluation set.
UTILIZATION: ai-safety-reviewer, api-contract-checker and workspace-isolation-auditor after implementation.
BEHAVIOR: Show the contract, the facade call, and where the analysis is stored; wait for approval; implement
with tests per source kind, mixed/absent sources, foreign-workspace sources, and an eval fixture for an
image carrying injected text; regenerate the OpenAPI snapshot.
```

## AF.3.5 Choose an existing picture as inspiration

```text
SCOPE: Fix the web half of FLU-003. In the shared reference panel used by Image Studio and the pipeline,
let the creator choose a picture from the DAM (reuse dam-asset-picker) or from this run's generated
pictures, alongside the existing Brand Library choice. The choice is stored as a reference on the context
and sent through AF.3.4.
CONSTRAINT: AF.3.4, AF.3.1; content-pipeline-document-picker and the reference panel; new-component skill.
RESTRICTION: One reference picture at a time unless the plan justifies more. The creator sees what the
analysis said and can discard it before it shapes a prompt. An inspiration picture is never copied,
re-uploaded, or presented as the output.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the three-source chooser and its states, wait for approval, implement with specs for each
source, removal, and an analysis failure.
```

---

# Phase 4 — Save generated pictures to the DAM

## AF.4.1 Web service and "Save to library" dialog

```text
SCOPE: Add the missing web caller for POST /dam-assets/from-generated-image: a create method on
dam-asset.service.ts with its models, and one shared dialog that collects what the route accepts — title,
alt text, tags, an optional recipe link, and whether to keep the prompt in the prompt library — and saves.
CONSTRAINT: FLU-002, FLU-005; DamAssetsController and MediaAssetBusiness.CreateFromGeneratedImageAsync
(built and covered by CreativePipelineEndToEndTests); .claude/rules/media.md; new-component skill.
CpDialogComponent hosts content; ConfirmService is for confirmations.
RESTRICTION: Web only. Alt text is the creator's: it may be pre-filled only from a stored analysis of that
picture's pixels, marked as generated and editable, and is never invented from the prompt, the filename, or
the recipe. Defaults come from the context (recipe link, title). Saving twice must produce one asset — send
the idempotency key the route expects and treat "already kept" as success that opens the existing asset.
UTILIZATION: design-review after implementation.
BEHAVIOR: Inspect the route's request shape and its already-kept behaviour, show the dialog fields and
states, wait for approval, implement with service and component specs including double-submit and a
partial-validation error mapped to its field.
```

## AF.4.2 Save to library from Image Studio - done

*Delivery note, in four parts.*

*The run became the filing surface rather than the studio.* `cp-generated-image-run` takes one new input,
`library: GeneratedImageRunLibrary | null`, which carries the default title, the recipe to offer as the link,
and the work's context references. Null — the Content Pipeline, until AF.4.3 — keeps the keeper checkbox and
changes nothing. Non-null replaces that checkbox with a per-picture save: marking and saving are one action, so
**the studio writes no keepers at all** and `ContentPipelineImagesState` is untouched. AF.4.3 supplies the same
input from the pipeline's library step.

*A saved picture is saved because the server says so.* `GeneratedImageStatus.Kept` is the authority and
survives a reload, another tab and another device. The asset's id is the one thing a staged row does not carry,
so it is handed over by a save made here and otherwise matched back through the work's own `DamAsset`
references — `DamAssetDetail` now reads `sourceGeneratedImageId` for exactly that, read one reference at a time,
stopped as soon as every saved picture is accounted for, and not read at all when nothing is saved. A picture
whose asset cannot be found still says it is in the library, with the way to the library rather than to the
asset.

*No prompt is offered with the save, and that is deliberate rather than deferred by oversight.*
`PromptRecordInputChecks` requires a non-`Manual` prompt to name its proposal, the model's draft and the
template id, version and checksum together, and a run keeps request ids rather than any of those — so the tick
would have been a refusal waiting to happen. AF.4.3 assembles that lineage. The dialog gained one small output
for this prompt, `progress`, because a save in flight and one that did not land are the two states no picture
row can carry.

*One interaction to know about, not introduced lightly.* `pictureReferenceOf` reads the first `DamAsset` or
`GeneratedImage` reference as "the picture this work takes its cues from" (AF.3.5), so a saved output shows up
in the reference panel as an inspiration picture when the creator had not chosen one. An existing choice is
never displaced — the save is appended — and nothing is analysed or sent to a model until the creator asks, so
no prompt is shaped by it. Worth a role on the reference, or a filter, when AF.3.5 or AF.4.3 next touches that
convention.

*Closed by AF.4.3*, which gave references a purpose: a saved asset is a `Keeper` and `pictureReferenceOf`
reads only a `Source`, so the studio's output is no longer offered back as a cue.

```text
SCOPE: Fix FLU-005. In Image Studio's generated-image run, add "Save to library" per picture using AF.4.1,
show a saved picture as saved with a link to its DAM asset, and attach the DAM asset to the context.
CONSTRAINT: AF.4.1; image-studio.component and cp-generated-image-run.
RESTRICTION: Remove the "Nothing here files a picture in the library" copy and comment once untrue. A saved
picture can no longer be declined from this screen. Marking a keeper and saving are one action here, not
two. Staged pictures that are not saved still expire, and the screen says when.
BEHAVIOR: Show the per-picture states (staged, saving, saved, failed), wait for approval, implement with
component specs.
```

## AF.4.3 Content Pipeline library step - done, in two commits

*Delivery note, in five parts.*

*The keeper move needed a server change first, and it was not optional.* Before this, "the picture this work
takes its cues from" was *the first* picture reference on the context, and the reference panel **removes that
row** when the creator chooses a different cue — so keepers-as-references would have meant choosing an
inspiration picture silently deleting a keeper. References now carry a
`CreativeContextReferencePurpose` (`Source`, `Keeper`), defaulting to `Source`, with the two **picture** kinds
unique per purpose rather than per target: one picture can honestly be this work's output *and* the cue its
next prompt is planned from, while a recipe or a prompt is still named once however it is used. `Source` is
what an unstated purpose means, so an old body and a new one agree, and the whole thing is additive in the
OpenAPI snapshot. It also retires the interaction AF.4.2's note flagged — a saved asset is a `Keeper` and
stops reading as inspiration.

*Keepers are references; the run id stays on the device.* `ContentPipelineImagesState` is `{ operationId }`
now, the run's `keepers` became its own input with a `keepToggled` output, and the images step writes a mark
to the context. `CONTENT_PIPELINE_RUN_VERSION` and `IMAGE_STUDIO_WORK_VERSION` are raised, and a record from
the previous shape is discarded as every earlier one has been — its marks cannot be written to a context from
a decoder.

*A keeper whose picture has gone is no longer pruned.* `keepersStillPresent` and the run's two pruning effects
are deleted. A reference points at something that may have stopped being usable rather than at nothing, which
is what its own entity doc says it means, so the library step says "this picture was declined" or "held past
the time generated pictures are kept for" instead of quietly forgetting the creator chose it.

*Nothing about progress is stored, which is what makes resume honest.* A keeper is saved when its picture's
status is `Kept` — the server's own answer — and the asset it became is matched back through the work's
`DamAsset` references by `sourceGeneratedImageId`, the matcher now shared with AF.4.2 as
`matchSavedPictures`. So a resume retries exactly what is not saved, a replay costs nothing (the route keeps a
picture once and answers with the first asset), and each save is its own request, so one failure neither
undoes nor blocks the others.

*Two things worth knowing.* **The prompt sends no recipe pin**: the proposal already records which recipe and
version it was about, and a pin that disagreed — the creator relinked the recipe after composing — would be
refused, failing every picture alike to say something the proposal says better. Where the lineage cannot be
assembled honestly the picture still saves and the step says why (`no_channel` when the work has named no
channel, `unreadable` when the generation cannot be read); an edited *reference* reading records as `Manual`,
which understates it, because the alternative is permanently claiming a model wrote words a person may have
replaced. **A step that is not built is now passed through** rather than blocking Continue: the library step
is finished while `posts` before it is not, and stopping there would hide a built step behind an unbuilt one.
The journey's order is unchanged.



```text
SCOPE: Fix FLU-002. Replace the pipeline's "library" placeholder with a real step: the chosen keepers,
each saved through AF.4.1 with its prompt lineage and the run's recipe link, with per-picture progress and a
clear end state linking to the DAM.
CONSTRAINT: AF.4.1, AF.3.1; PIPE-UI-006 (DAM and prompt-lineage half only); content-pipeline.models step
list and content-pipeline-shell.
RESTRICTION: No content-board card and no scheduling — that remains master 13.7a. Several keepers save
independently: one failure does not undo or block the others, and resuming the step retries only what is
not saved. Keepers move from a string[] in the browser to references on the context.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the step states and the resume rule, wait for approval, implement with specs for all-saved,
partial failure, resume, and replay.
```

---

# Phase 5 — Smaller images, server-side, with no third-party package

## AF.5.1 Compression decision record and feasibility spike

```text
SCOPE: Decide, in writing, how FLU-007 is met without an external NuGet image package. Add a section to
docs/architecture-decisions/ai-fluency.md covering: (a) what the Venice image API can return — verify the
supported output formats, quality, and dimensions against current first-party documentation, since
VeniceImageGenerator sends format "png" and no size today; (b) the managed path — PNG decode on
System.IO.Compression, area-average downscale, baseline JPEG encode — with a throwaway spike in the scratch
directory measuring output size and time on real generated pictures; (c) the rendition set (a web rendition
and a thumbnail), target dimensions and quality; (d) which input formats are in scope.
CONSTRAINT: Decision 2; .claude/rules/media.md and external.md. CLAUDE.md: verify fast-moving APIs against
first-party documentation.
RESTRICTION: No production code and no package reference. System.Drawing is not a candidate. Be explicit
about what the no-package route cannot do: decoding WebP or progressive JPEG is out of reach of a small
managed codec, so a source in a format the codec cannot read is served as stored and reported as not
compressed. If the spike shows the managed path is not viable, stop and say so with the numbers rather than
adding a dependency.
BEHAVIOR: Report the provider findings with their source, the spike numbers, and a recommendation; wait for
approval before any later prompt in this phase runs.
```

## AF.5.2 Managed PNG decoder and resampler

```text
SCOPE: Add a pure, dependency-free PNG decoder and an area-average downscaler in the Media module: bytes in,
an RGBA pixel buffer out; buffer and target box in, a smaller buffer out. Supports the colour types and bit
depths the provider actually emits (per AF.5.1) and rejects everything else with a typed result.
CONSTRAINT: AF.5.1 approved; MediaPolicy limits (32 MB, 50 MP); GeneratedImageInspector for signature
sniffing. Lives under Modules/Media/Business or Managers as the plan justifies.
RESTRICTION: No package, no unsafe code, no I/O — pure functions over spans, with a CancellationToken
checked per row. Decoded size is bounded before allocation, so a small file declaring huge dimensions is
refused, not allocated. CRC and length are verified; a malformed chunk is a typed failure, never an
exception that escapes. Interlaced input is either supported or refused explicitly.
BEHAVIOR: Show the supported matrix and the allocation guard, wait for approval, implement with tests over
known-answer images per colour type, truncated and corrupted input, a decompression bomb, and downscale
results checked against expected pixels.
```

## AF.5.3 Managed baseline JPEG encoder

```text
SCOPE: Add a pure, dependency-free baseline JPEG encoder in the Media module: an RGB pixel buffer and a
quality setting in, a standards-conformant baseline JFIF byte stream out.
CONSTRAINT: AF.5.2; the quality and chroma-subsampling choice recorded in AF.5.1.
RESTRICTION: No package, no unsafe code, no I/O. A picture with any non-opaque pixel is not flattened
silently — the caller is told, and the rendition policy in AF.5.5 decides (keep PNG). Output must decode in
real browsers, not only round-trip through this codebase.
BEHAVIOR: Show the encoder stages and the test strategy, wait for approval, implement with tests for
structural validity (markers, tables, dimensions), size monotonic in quality, edge dimensions not divisible
by the block size, and a set of encoded fixtures opened in a browser through playwright-cli.
```

## AF.5.4 Media rendition entity and migration

```text
SCOPE: Add the workspace-owned MediaRendition: a derived encoding of one source picture — a staged
GeneratedImage or a MediaAssetVersion — with purpose (web, thumbnail), media type, byte size, dimensions,
object key, checksum, status, and failure reason.
CONSTRAINT: AF.5.1; .claude/rules/media.md and tenancy.md; add-workspace-entity and add-media-feature
skills.
RESTRICTION: The stored original is immutable and is never replaced or deleted by a rendition. One
rendition per source and purpose (unique within the workspace). Deleting, declining, or expiring the source
removes its renditions' bytes through the existing cleanup path. Keeping a staged image carries its
renditions to the new asset version rather than recomputing them.
BEHAVIOR: Show the entity, indexes, cascade behaviour and the keep-carries-renditions rule; wait for
approval; implement configuration and migration; review and apply; test uniqueness, cleanup, carry-over,
and two-workspace isolation.
```

## AF.5.5 Rendition worker and backfill

```text
SCOPE: Add the durable job that produces renditions: after a generated image is stored, and after a DAM
version is added, decode, downscale, encode, store, and record each rendition. Include a bounded backfill
for pictures stored before this existed.
CONSTRAINT: AF.5.2–AF.5.4; add-background-job skill; the outbox and the existing queue-claim pattern in
GeneratedImageWorkerHostedService.
RESTRICTION: Idempotent — a retried job produces no duplicate rendition or orphaned object. A queue claim
returns identifiers only, and the worker validates the workspace through the ordinary tenancy path before
reading anything else; any new IgnoreQueryFilters use is listed in BulkOperationBoundaryTests.Exemptions.
A source the codec cannot read is recorded as "not compressed" with a reason and is not retried forever.
A rendition larger than its source is discarded. Generation never waits on compression.
UTILIZATION: add-background-job; workspace-isolation-auditor after implementation.
BEHAVIOR: Show the trigger, claim, retry and backfill plan; wait for approval; implement with tests for
success, unreadable source, retry after a crash between store and record, backfill resumption, and
two-workspace processing.
```

## AF.5.6 Serve renditions and ask the provider for less

```text
SCOPE: Make the smaller picture the default. The staged-image preview and content routes and the DAM render
route serve the web rendition when one exists and the stored original otherwise; lists and pickers use the
thumbnail; download offers both "web size" and "original". If AF.5.1 found the provider supports it, request
the smaller format and explicit dimensions in VeniceImageGenerator. The web shows each picture's size.
CONSTRAINT: AF.5.5; .claude/rules/api-contract.md, gateway.md and external.md.
RESTRICTION: Additive contract — an explicit parameter selects the original, the default changes only in
bytes served, and the response states which it is. Personalised responses are not cached at a shared proxy.
Provider options stay inside CreatorPantry.AiProvider and no provider field reaches a core entity.
UTILIZATION: api-contract-checker and integration-reviewer after implementation.
BEHAVIOR: Show the route behaviour table and the provider request change, wait for approval, implement with
tests for each route with and without a rendition, the original selector, and a two-workspace check;
regenerate the OpenAPI snapshot; report before/after byte sizes for a real run.
```

---

# Phase 6 — The pipeline writes the posts

## AF.6.1 Post package model

```text
SCOPE: Add workspace-owned SocialPackage and SocialRevision in the Content module. A package belongs to a
CreativeContext and holds one output per creator-selected channel key; a revision is one generated or
edited body for one channel, with character-limit result, template/model provenance, status, acceptance,
and the exact source versions it was written from. Staleness follows the pinned recipe version.
CONSTRAINT: FLU-001; Decision 1; supersedes master 13.1; .claude/rules/content.md and tenancy.md;
add-workspace-entity and add-content-feature skills.
RESTRICTION: Accepted history is immutable — regeneration and edits create new revisions. Post copy never
becomes canonical recipe data. No provider scheduling id. Channel keys are validated against
ContentChannelCatalog and a retired key already stored keeps working. Each channel is accepted or rejected
on its own.
BEHAVIOR: Show lifecycle, indexes, and the staleness rule; wait for approval; implement configuration and
migration; review and apply; test immutability, per-channel acceptance, staleness after a recipe change, and
two-workspace isolation.
```

## AF.6.2 Channel writing profiles

```text
SCOPE: Give each channel in ContentChannelCatalog a deterministic writing profile — maximum length,
hashtag policy, link and markup rules, and the kind of output it is (caption, pin description, blog intro,
newsletter blurb) — and a validator that scores a body against it.
CONSTRAINT: Decision 1 (confirm the short-form blog/newsletter reading here); .claude/rules/content.md
("channel constraints belong in channel profiles/adapters, not scattered prompt text"); ContentChannels.cs
and ContentChannelCatalogTests. Verify each platform limit against current first-party documentation and
cite it in the code.
RESTRICTION: Limits are code, not prompt text, and counting follows each platform's own rule where it
differs from string length. Shared kernel stays free of module dependencies.
BEHAVIOR: Show the profile table with sources, wait for approval, implement with tests per channel at,
under, and over the limit, including multi-codepoint characters.
```

## AF.6.3 `content.channel-posts` capability

```text
SCOPE: Add the capability that writes posts for the selected channels: a versioned prompt file, an output
schema with one entry per requested channel, a task handler registered in AiTaskCatalog, and an evaluation
set. Grounding is the AF.1.5 package plus the approved BrandContextPackage.
CONSTRAINT: FLU-001, FLU-003; supersedes master 13.2; .claude/rules/ai.md and recipes.md; add-ai-capability
skill; AF.6.1–AF.6.2. Memory: ban the claim frame, not the food word, when writing refusal fixtures.
RESTRICTION: The model writes copy only. Length, hashtag policy, and link rules are enforced by the AF.6.2
validator after generation; an over-limit body is returned flagged, never silently trimmed. It may not
invent recipe facts, quantities, times, nutrition, allergen or safety claims, search metrics, or anything
about a picture beyond what the package says about it. A channel the request did not name is never written.
Output is a proposal until accepted. Prompt bodies and generated copy are not logged.
UTILIZATION: add-ai-capability; ai-safety-reviewer after implementation.
BEHAVIOR: Show the schema, the prompt's declared inputs and outputs, and the fixture list; wait for
approval; implement with evaluation fixtures for quality, over-limit output, an unrequested channel,
missing source, a food-safety or allergen claim, prompt injection through each untrusted section, stale
source, and workspace isolation.
```

## AF.6.4 Post request, status, and disposition endpoints

```text
SCOPE: Add the request seam: POST a post-writing request for a creative context and a set of channel keys
(202 with an operation), GET status and the resulting package, PATCH a creator edit to one channel as a new
revision, and POST a per-channel disposition (accept, reject, regenerate).
CONSTRAINT: AF.6.1–AF.6.3; the shared AI lifecycle and allowance admission (9A); .claude/rules/
api-contract.md; add-endpoint skill.
RESTRICTION: Requests take an Idempotency-Key that names the request — the same key with different channels
or context is refused. An exhausted allowance is refused at admission, before any provider call. Accepting
twice does not create two accepted revisions. Regenerating one channel leaves the others untouched.
UTILIZATION: api-contract-checker and workspace-isolation-auditor after implementation.
BEHAVIOR: Show routes, shapes, and error codes; wait for approval; implement the full seam with tests for
idempotency, per-channel disposition, cancellation marking the outcome accurately, allowance refusal, and
two-workspace isolation; regenerate the OpenAPI snapshot.
```

## AF.6.5 Content Pipeline posts step

```text
SCOPE: Fix FLU-001. Replace the pipeline's "posts" placeholder: choose channels (defaulting to the brand
profile's channel defaults), generate, then review each channel on its own — body, count against the limit,
generated marker, edit, regenerate, accept, copy.
CONSTRAINT: AF.6.4; supersedes master 13.3a; .claude/rules/frontend.md and design-system.md; new-component
and creatorpantry-design-system skills. Build the per-channel review as a shared composition so master 13.3
(Social Studio) can reuse it.
RESTRICTION: Nothing appears accepted automatically. Copy uses the accepted or currently edited text, never
a stale raw generation. An unsaved edit survives a failed save, a regeneration of another channel, and
navigation, with the standard warning. Limit status is conveyed by more than colour. No scheduling.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the step states and the per-channel state machine, wait for approval, implement with
component, contract, accessibility, and error-path specs.
```

## AF.6.6 Posts that know the picture

```text
SCOPE: Complete FLU-003 for writing. When the run has a kept or chosen picture, the posts step shows it and
offers "write to this picture"; the request then includes that picture's stored analysis through the
AF.1.5 package, requesting an analysis first (AF.3.4) when none exists.
CONSTRAINT: AF.6.3–AF.6.5, AF.3.4.
RESTRICTION: Without a stored analysis the capability is told only that a picture accompanies the post and
must not describe it. The analysis is shown to the creator before it is used. Bump the prompt version and
update the evaluation set if behaviour changes.
BEHAVIOR: Show the with/without-analysis package difference, wait for approval, implement with an eval
fixture proving no visual detail appears without an analysis.
```

---

# Phase 7 — Themed days and their memory

## AF.7.1 Standing notes on a themed day

```text
SCOPE: Fix the first half of FLU-009. Let a WorkspaceWeeklyTheme carry the creator's standing instructions
for that day — free text they write, e.g. "Meatless Monday: always under 30 minutes, never tofu" — and
extend the weekly-themes read and write routes to return and accept it.
CONSTRAINT: master 12.1a (built: one theme per day, GET/PUT/DELETE weekly-themes); .claude/rules/
tenancy.md and api-contract.md; add-workspace-entity and add-endpoint skills.
RESTRICTION: Additive, optional, length-capped. Notes are creator intellectual property: workspace-scoped,
never seeded, never written or rewritten by a model. Editing notes bumps the theme's revision. The existing
retirement and stored-key behaviour is unchanged.
BEHAVIOR: Show the column, cap, and contract change; wait for approval; implement configuration and
migration; review and apply; test round-trip, cap, revision bump, and two-workspace isolation; regenerate
the OpenAPI snapshot.
```

## AF.7.2 Day history read model

```text
SCOPE: Add the second half of the memory: a query answering "what has this workspace already made for this
themed day" — recent creative contexts carrying that theme key that reached an outcome (a kept picture, an
accepted post package, an accepted recipe draft), newest first, each as a short summary with links.
CONSTRAINT: AF.1.2, AF.4, AF.6.1; add-endpoint skill. A read model over existing records through their
facades — no new table unless the plan shows the query cannot be served otherwise.
RESTRICTION: History is derived, never authored, and cannot be edited into saying something that did not
happen. Bounded by count and age. A retired theme's history remains readable. Cursor-paginated. Workspace
cache keys start with the workspace id and are invalidated on the outcomes that change the answer.
UTILIZATION: workspace-isolation-auditor after implementation.
BEHAVIOR: Show the query, its bounds, and the cache and invalidation plan; wait for approval; implement with
tests for ordering, bounds, a retired theme, cache invalidation, and two-workspace isolation.
```

## AF.7.3 Themed days screen

```text
SCOPE: Build the missing screen for themed days: seven days, each with its theme name, description, and
standing notes, add/edit/retire/remove, and that day's recent history from AF.7.2. Add the typed web service
and a route under workspace settings, linked from Content Pipeline setup's day control.
CONSTRAINT: AF.7.1–AF.7.2; .claude/rules/frontend.md and design-system.md (one card, a legend, named
sections from CpFormSectionComponent; prose fields stay stacked); new-component and
creatorpantry-design-system skills.
RESTRICTION: A workspace with no themes sees a first-use empty state, not seven blank forms. Days without a
theme are normal, not an error. Removing a theme is confirmed through ConfirmService and says what happens
to work that already used it. Dirty state and validation are per form.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the layout and every state, wait for approval, implement with component specs and
accessibility checks in both themes.
```

## AF.7.4 Generation follows the day

```text
SCOPE: Make themed days shape generation. When a creative context has a day and theme, the AF.1.5 package
gains a day section — theme name, description, standing notes, and a bounded "already made" list from
AF.7.2 — and recipe.concepts, image.photography-concept, image.prompt, and content.channel-posts are given
it with the instruction to follow the notes and avoid repeating the list.
CONSTRAINT: AF.1.5, AF.7.1–AF.7.2; .claude/rules/ai.md; add-ai-capability skill. Today only the theme's
display name reaches generation, through ContentSeedDescription.
RESTRICTION: Notes and history are untrusted prompt content, delimited, and cannot redefine tool
permissions or override safety restrictions. History contributes titles and subjects only — never post
bodies or recipe text. Each changed prompt takes a new version with its evaluation set updated and run.
Each surface shows the creator that the day's notes are in effect and lets them switch them off for one
run. The deterministic seed generator is unchanged.
UTILIZATION: ai-safety-reviewer after implementation.
BEHAVIOR: Show the day section, its caps, and the four template diffs; wait for approval; implement one
capability per turn with fixtures for notes followed, repetition avoided, an injection string in the notes,
no theme set, and workspace isolation.
```

---

# Phase 8 — Proving it connects

## AF.8.1 Connected-journey end-to-end test

```text
SCOPE: Add one HTTP-level test in the style of CreativePipelineEndToEndTests that runs the whole journey for
Workspace A — concept request, chosen concept, draft and acceptance into a recipe, creative context from
that recipe on a themed day, photography concept and prompt with the recipe ids, generation, worker pass,
renditions, save to DAM, post request for two channels, per-channel acceptance, day history — while
Workspace B attempts every step against A's ids and reaches none of them.
CONSTRAINT: Every earlier phase; .claude/rules/tenancy.md (Required tests). Memory: SQL container
contention can red a class in a full run — re-run it alone before believing a failure.
RESTRICTION: A test, not a new feature — where a step cannot be driven over HTTP, report the gap instead of
reaching into a repository. Model and image providers are the test doubles the existing suite uses.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the step list and what B attempts at each; wait for approval; implement; run the class alone
and then the full suite; report.
```

## AF.8.2 AI fluency audit

```text
SCOPE: Audit, do not build. Run the read-only reviewers over everything this document delivered and walk
the nine feedback items in the running app, recording for each whether a creator can now do it without
retyping anything.
CONSTRAINT: `aspire run`; `dotnet run --project src/CreatorPantry.Tests`; `npm run build` and `npm test`
from src/web/.
RESTRICTION: Fix blocker-level findings only when they are small and local; anything larger becomes a
written follow-up prompt appended to this file. Do not mark a feedback item resolved from tests alone.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, ai-safety-reviewer, api-contract-checker,
design-review, test-gap-analyzer, skills-evals; playwright-cli for the walk-through.
BEHAVIOR: Report per feedback item (resolved / partly / not), per reviewer (findings by severity), and the
follow-up prompts written. Update the checkpoint table and the `- done` markers.
```
