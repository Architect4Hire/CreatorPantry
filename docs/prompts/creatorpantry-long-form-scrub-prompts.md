# CreatorPantry — Long-Form Content SCRUB Prompts

Atomic prompts for giving CreatorPantry a long-form writer: a full blog article and a full newsletter issue,
written from a recipe, from the creator's own brief, or from a saved prompt — for use with Claude Code and
the CreatorPantry `.claude/` toolkit.

This document supplements `docs/prompts/creatorpantry-scrub-microprompts.md` (the "master library") and runs
**after `docs/prompts/creatorpantry-ai-fluency-scrub-prompts.md` (the "AF document") and before master
Phase 13**. It does not redefine anything decided in `CLAUDE.md` or `.claude/rules/`. Where it overlaps a
master or AF prompt, the overlap is listed under
[Relationship to the existing libraries](#relationship-to-the-existing-libraries) and LF.1.1 records it in
both files.

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

## The gap this answers

Creator feedback received 2026-10-10: *the blog type for the content pipeline only writes up to 600
characters — there is no way to draft a full newsletter or blog post.*

The 600 is not a defect. It is `ContentChannelProfileCatalog`'s `blog` profile —
`ChannelOutputKind.BlogIntro`, 600 characters, `ChannelLimitOrigin.Editorial` — and B-27 decided it on
2026-10-10. `newsletter` is a 400-character blurb beside it. Both are deliberate short-form teasers for the
channels a creator publishes to, and the prompt agrees with the code: *"a blog introduction reads like the
opening of a post, a newsletter blurb like a short note to a subscriber."*

The defect is that **nothing anywhere writes the post the teaser is a teaser for.** B-27 deferred long-form
to `content.editorial-package` (master 11.4), and the AF document lists "long-form blog articles and SEO
packages (master 11.4–11.5)" as out of bounds for the same reason. But 11.4 writes an article's *parts* —
headnote, introduction, tips, substitutions, storage/reheating, FAQ, call to action — and never an article.
The deferral pointed at a prompt that was never about long-form. This document is the missing piece.

| ID | Requirement | Phase |
| --- | --- | --- |
| LFC-001 | A long-form draft artifact, rooted on a creative context, that can be started from a recipe, a brief, or a saved prompt. | 1 |
| LFC-002 | Grounding for long-form, including reuse of an accepted editorial or SEO package. | 1 |
| LFC-003 | A reviewable section plan the creator approves before any prose is written. | 2 |
| LFC-004 | Full-length prose, written and regenerated one section at a time. | 3 |
| LFC-005 | One assembled document the creator reads, edits, and accepts as a whole. | 3 |
| LFC-006 | A newsletter issue that may cover more than one source. | 4 |
| LFC-007 | Markdown and HTML export of an accepted long-form draft, with its SEO package. | 5 |
| LFC-008 | Staleness when a pinned source version moves. | 6 |
| LFC-009 | The surfaces: a Content Pipeline step and a Workflows Hub entry. | 6 |

## Decisions this document assumes

**None of these has been confirmed by the product owner.** They are the author's reading of the feedback and
of the code as it stands. LF.1.1 records them as B-32 through B-37 and is the point at which they are
confirmed or corrected; if one is wrong, stop at LF.1.1 rather than carrying it into an entity.

### B-32 Long-form is its own artifact, rooted on a creative context

Not a channel post, and not a new `ContentPackageKind`. A `LongFormDraft` hangs off a `CreativeContext`, the
way `SocialPackage` does.

*Why not raise the `blog` ceiling:* the channel profile's output kind, counting rule, and prompt all say
"intro". A 5,000-character `BlogIntro` would be a long teaser sitting inside a post package, with no section
structure, no outline, no per-section regeneration, and no link to an SEO package.

*Why not a `ContentPackageKind`:* `ContentProposal` requires a `RecipeId`, allows exactly one proposal per
recipe and kind, and `ContentRevision` repeats `RecipeId` so its recipe-version pin can be constrained by a
composite key. Long-form has to work with no recipe at all ("or the prompt"), and a creator writes more than
one newsletter about the same recipe. Making `RecipeId` nullable would weaken a shipped invariant on
editorial and SEO to serve a different artifact.

*What this preserves:* B-27 stands untouched. `blog` stays a 600-character intro and `newsletter` a
400-character blurb, and they become the teasers that point at the long-form piece.

### B-33 Outline first, then one section at a time

A long article in one model call is neither reviewable nor reliable. The lifecycle is:

```text
Draft → Outlined → Writing → Assembled → Accepted
                                      └→ NeedsReview (a pinned source moved)
```

The creator approves or edits a section plan before any prose exists. Each section is then a generation of
its own, regenerable without touching its neighbours, with a "write the rest" convenience that enqueues one
operation per remaining section in order.

**Acceptance is of the document, not of a section.** One accepted revision is the article of record. A
section carries a creator-set *settled* flag so the UI can show progress, which is a working state and not a
second acceptance semantics.

### B-34 Length is an editorial target; channel profiles are untouched

`ContentChannelProfiles.cs` is not edited by this document. Long-form ceilings are new editorial constants.

Hard ceilings, enforced in code after generation, never trimmed silently: **3,000 characters per section,
20,000 per document.** Target word ranges, passed to the prompt as guidance and reported as a warning when
missed rather than a failure: **blog article 900–1,600 words, newsletter issue 300–700 words.** The four
figures are proposals and need the owner's number.

### B-35 A newsletter issue may cover more than one source

A food newsletter is usually a roundup: two or three recipes, a note from the creator, a link out. A
newsletter issue therefore takes an ordered set of the context's references, one `item` section each, plus
the creator's own framing. **This is the assumption most likely to be wrong**; if an issue is always about
one recipe, Phase 4 collapses into a mode of Phase 3 and should be cut.

### B-36 Accepted editorial and SEO packages are inputs, not competitors

When a referenced recipe already has an accepted editorial revision, the long-form draft is given it and
told which sections are already written. It reuses that prose, attributes it in the assembled document, and
does not regenerate the same material under a new heading. Long-form never writes back to a
`ContentProposal`.

### B-37 Long-form text is Markdown in a plain control; no rich-text editor

Sections are edited as plain Markdown in a `textarea` inside `CpFieldComponent`. No WYSIWYG, no
contenteditable, no new frontend dependency. The design system has no rich-text primitive and this document
does not add one.

## How this list was built

A code map taken on 2026-10-10 (branch `dev`, after `d758e9a AF Prompts 6.4 - 6.6`). File references below
come from that map — re-find them before relying on a line number.

The honest finding: **the material for a long-form post is almost all there, and nothing composes it.**

| Fact | Where |
| --- | --- |
| `blog` is `BlogIntro`/600 and `newsletter` is `NewsletterBlurb`/400, both `Editorial` origin. | `Managers/Reference/ContentChannelProfiles.cs` |
| The editorial package writes seven named sections; 2,000 characters per prose section, 600 per list item, 5 tips, 5 substitutions, 6 FAQ items. Assembled, roughly 8–10k characters of real body. | `AiPolicy.cs`, `AiEditorialPackageOutputDocument.cs` |
| All 17 real `AiTaskType` members, through `ChannelPosts = 17`. None writes an article or a newsletter. | `Modules/Ai/Managers/AiTaskType.cs` |
| `RecipeMarkdownExporter` already assembles recipe plus every accepted editorial section into one document — but recipe-first, and every editorial heading is labelled `(editorial)`, which is review scaffolding rather than publishable copy. | `Modules/Recipes/Managers/RecipeMarkdownExporter.cs` |
| `ContentProposal` is one slot per recipe and kind, with a required `RecipeId`; `ContentRevision` is immutable, holds versioned JSON of named sections, and pins recipe version, brand profile, style guide, and prompt template with its body checksum. The shape to mirror. | `Modules/Content/Data/Entities/` |
| `SocialPackage` is rooted on a `CreativeContext` with one output per channel key. The rooting to mirror. | `Modules/Content/Data/Entities/SocialPackage.cs` |
| The pipeline is six steps — setup, idea, prompt, images, posts, library — all built. | `src/web/src/app/models/content-pipeline.models.ts` |
| Master 13A.18 "Write a blog post and SEO package" is a *wizard shell* prompt: it composes selection, editorial review, SEO review, JSON-LD, preview, export. It has no writing step of its own. | master library |

## Prerequisites already satisfied

Reuse these; do not rebuild them:

- `CreativeContext`, `CreativeContextReference`, `CreativeContextChannel`, and the deterministic
  `ICreativeContextPackageFacade` assembler (AF.1.2–AF.1.5). "From the creator's own brief" is already a
  first-class field there — `WorkingBrief` with its `BriefSource` — and "or the prompt" is the
  `PromptRecord` reference kind. Neither needs inventing.
- The AI operation, proposal, disposition, and worker lifecycle (`Modules/Ai`), `AiTaskCatalog`, the
  versioned prompt loader (`Managers/Prompts/`, B-16), the untrusted-context envelope, and the evaluation
  harness under `src/CreatorPantry.Tests/Ai/Evaluation/`.
- `BrandContextAssembler` / `BrandContextPackage` and `IAiQuotaAdmissionFacade` (9A) for admission.
- `ContentProposal` / `ContentRevision` / `ContentProposalTransition` and `IContentEditorialFacade`,
  `IContentSeoFacade`, `IContentStalenessFacade`.
- `AiEditorialClaimScanner` and `AiSeoClaimScanner` — the long-form scanner extends this pattern, it does
  not replace it.
- `RecipeMarkdownEscaper` and `RecipeMarkdownExporter` for export conventions.
- The per-channel review composition from AF.6.5 and `cp-generated-image-run`, `CpFormSectionComponent`,
  `CpAnchorNavComponent`, `CpDiffLegendComponent`, `CpNoticeComponent`, `CpStatusPillComponent`.

## Relationship to the existing libraries

| Prompt | Effect of this document |
| --- | --- |
| AF document, "Out of bounds" | **Amended by LF.1.1.** Long-form is no longer deferred to master 11.4; it is this document. |
| B-27 (`ai-fluency.md`) | **Unchanged and reaffirmed.** `blog`/`newsletter` stay short-form. LF.1.1 adds a pointer saying where long-form went. |
| master 11.4 Editorial package AI task | **Unchanged.** Its seven sections remain; LF.1.4 and B-36 make an accepted editorial package an input to long-form. |
| master 11.5 SEO package AI task | **Extended by LF.5.3** to take a long-form draft as its subject as well as a recipe. No change to its validators or claim rules. |
| master 11.7 Markdown export | **Unchanged.** LF.5.1 adds a separate long-form export; the recipe export's bytes must not move. |
| master 11.3 Derivative staleness propagation | **Extended by LF.6.1** to long-form drafts. |
| master 13.3 Social Studio UI | Unchanged. |
| master 13A.18 "Write a blog post and SEO package" | **Narrowed.** It stays the wizard shell; the writing steps it composes are LF.2–LF.5. |
| AF.6.1–AF.6.6 | Unchanged. The posts step gains a teaser-for-the-article relationship in LF.6.2. |

## Out of bounds for this document

- Scheduling, the content board, and publishing (master Phase 13). This document ends at an accepted draft
  and an export.
- Any public or consumer-facing rendering of an article. CreatorPantry is creator-first.
- A rich-text editor (B-37), a second diff engine, and any new frontend package.
- Nutrition, allergen, dietary, or food-safety conclusions, in prose or in structured output.
- Images inside the article body. An article references media the DAM already holds; it does not generate or
  place pictures. Figure placement is a follow-up.
- Translation and locale variants of a finished article.

## Credentials and external prerequisites

A Foundry chat deployment, as for master Phase 11 and AF Phase 6. Embeddings are not needed. Evaluation
fixtures run without a live provider.

**Token cost is materially higher than any existing capability** — an article is a dozen generations where a
post package is one. LF.3.2 must hold admission per section and LF.3.3 must show the creator what "write the
rest" will spend before it spends it.

## Checkpoints

| After | Demonstrable result |
| --- | --- |
| **LF.1.4** | A long-form draft can be started from a recipe, from the creator's own brief, or from a saved prompt, and its grounding package shows exactly what the writer may use — including an accepted editorial package — and never crosses a workspace. |
| **LF.2.3** | A creator gets a reviewable section plan, edits it, and approves it. No prose exists until they do. |
| **LF.3.4** | A full-length article exists, written section by section, each regenerable on its own, assembled into one document the creator reads whole and accepts once. |
| **LF.4.3** | A newsletter issue covering two recipes and the creator's own framing, from one creative context. |
| **LF.5.3** | An accepted article exports as Markdown and HTML with its SEO package, and the recipe Markdown export is byte-identical to before. |
| **LF.6.2** | The pipeline writes the article and the teaser posts in one run, and the teasers point at the article. |
| **LF.7.2** | One recorded journey — recipe → context → outline → sections → assembled → accepted → export — passes end to end over HTTP for two workspaces that cannot see each other. |

---

# Phase 1 — Decisions and the long-form artifact

## LF.1.1 Decision record and library reconciliation

```text
SCOPE: Documentation only. Add docs/architecture-decisions/long-form.md recording B-32 through B-37 above in
the baseline.md format, with the alternatives rejected under B-32 and the four open numbers under B-34. In
docs/architecture-decisions/ai-fluency.md, annotate B-27 with a one-line pointer saying long-form moved here
and that the short-form reading stands. In docs/prompts/creatorpantry-ai-fluency-scrub-prompts.md, amend the
"Out of bounds" line that defers long-form to master 11.4–11.5. In the master library, annotate 11.4, 11.5,
11.7, 11.3 and 13A.18 with one-line pointers per "Relationship to the existing libraries", and move the
"pick up here" marker.
CONSTRAINT: docs/architecture-decisions/baseline.md for the record format and decision numbering — B-31 is
the highest in use; .claude/rules/content.md and ai.md.
RESTRICTION: Do not delete or rewrite any superseded or annotated prompt — annotate it. Do not record a
decision this document does not state. No code, no entity, no migration.
BEHAVIOR: Show the record outline and every file edit, wait for approval, write them, and report any
decision the owner corrected together with every later prompt in this document that the correction changes.
A corrected B-32 or B-35 stops the document until the affected prompts are rewritten.
```

## LF.1.2 LongFormDraft entities and migration

```text
SCOPE: Add the workspace-owned LongFormDraft and the immutable LongFormRevision in the Content module. A
draft belongs to one CreativeContext and has a LongFormKind (BlogArticle, NewsletterIssue), a status over
the B-33 lifecycle, the accepted revision id, stale-since and stale-reason columns, a row version, and
created-by. A revision is one whole state of the document: versioned JSON holding the section plan and each
section's heading, role, body, settled flag and per-section provenance, plus columns pinning every source it
was written against — recipe version ids for each recipe reference, brand profile revision, style guide
version, prompt template id/version/body checksum, and the creative context row version.
CONSTRAINT: LFC-001; B-32, B-33; .claude/rules/tenancy.md, backend.md, content.md; add-workspace-entity and
add-content-feature skills. Mirror ContentProposal/ContentRevision exactly — IWorkspaceOwned,
IImmutableRecord, sequential RevisionNumber from 1, ParentRevisionId null only for revision 1, no
"current revision" column. Memory: check a new migration against a SQL Server fixture for multiple cascade
paths and keep CHECK constraints portable.
RESTRICTION: Revisions are immutable: every update and delete is refused at SaveChanges, and an edit, a
regeneration and a reaffirmation are each a new revision. A draft holds no recipe facts and never writes
back to Recipe, RecipeVersion or ContentProposal. No provider id, no scheduling field, no publication state.
Section roles come from a closed vocabulary (LF.1.3) stored as numbers, appended and never renumbered. A
reference the context later loses must not break an existing revision, because the revision pinned it.
UTILIZATION: add-workspace-entity; architecture-reviewer and workspace-isolation-auditor after
implementation.
BEHAVIOR: Show the entities, the JSON shape, the section-role vocabulary, indexes, delete behaviours and the
staleness columns' contract; wait for approval; implement configuration and migration; review and apply it;
restart aspire run so MigrationService applies it; test immutability, revision numbering, pin requirements,
a dangling context reference, concurrency, and two-workspace isolation.
```

## LF.1.3 Section roles and the long-form policy

```text
SCOPE: Add the deterministic shape rules in the Content module's Managers area: the closed section-role
vocabulary per kind, the B-34 ceilings and target word ranges, and a validator that scores a document
against them. Blog article roles: hook, context, whyItWorks, ingredientNotes, methodNarrative, tips,
variations, storage, faq, cta, closing, each at most once, plus up to four creator-headed body sections.
Newsletter issue roles: greeting, lead, item (repeatable, one per source reference), aside, closing,
signoff. Every section carries a creator-visible heading and a one-line intent.
CONSTRAINT: LFC-003, LFC-004; B-34; .claude/rules/content.md ("channel constraints belong in channel
profiles/adapters, not scattered prompt text") and recipes.md; ContentChannelProfiles.cs as the pattern for
code-owned limits with a Version string that changes whenever a figure does.
RESTRICTION: ContentChannelProfiles.cs is not edited and B-27's figures are not touched. Ceilings are code
and never prompt text; the prompt may be told a target, and it is the validator after generation that
decides whether a document meets it. A section over its ceiling is returned flagged, never trimmed. Word
counting is documented and deterministic — state the rule for hyphens, numerals and Markdown syntax — and a
missed word target is a warning, never a failure. The shared kernel stays free of module dependencies.
BEHAVIOR: Show the role table per kind, the ceilings, the word-count rule and the warning-versus-failure
split; wait for approval; implement with tests per role at, under and over each ceiling, a repeated
single-use role, an unknown role, a body section over the cap of four, multi-codepoint characters, and the
word-count rule's documented edge cases.
```

## LF.1.4 Long-form grounding package

```text
SCOPE: Extend the AF.1.5 CreativeContextPackage assembler with a long-form section, exposed through the
Content facade: the creator's own words and working title, every resolved recipe reference's facts from its
pinned version, the accepted editorial revision for each referenced recipe when one exists and which of its
seven sections it carries, the accepted SEO revision's key phrases when one exists, a referenced prompt
record's text, a referenced picture's stored analysis or creator-written alt text, the day's theme and
standing notes, and the approved BrandContextPackage.
CONSTRAINT: LFC-002; B-36; AF.1.5; .claude/rules/ai.md (Grounding) and tenancy.md; add-ai-capability skill.
RESTRICTION: Deterministic — no model call inside the assembler. Every reference is re-read through its
owning facade in the caller's workspace at assembly time; one that no longer resolves is dropped and
reported in the package, never guessed. Cross-module traffic is facade to facade. Each section has a
documented size cap and truncation rule, and the whole package has a budget — an issue referencing five
recipes must degrade by dropping whole references and saying so, not by truncating every one into
uselessness. Everything creator-written or retrieved is delimited as untrusted content and cannot redefine
tool permissions. A picture contributes only what a stored analysis or creator-written alt text says; with
neither, "a picture is attached, not described". With no recipe reference at all the package says so
explicitly, so the writer knows it has no recipe facts to draw on. The package records exactly which record
versions it used.
UTILIZATION: add-ai-capability; ai-safety-reviewer and workspace-isolation-auditor after implementation.
BEHAVIOR: Show the package shape, the caps, the whole-package budget and degradation order, and the per-task
allow-list; wait for approval; implement with tests for determinism (same inputs, same bytes), truncation,
budget degradation with five references, a dropped reference, an accepted editorial package present and
absent, an injection string in each creator-written and retrieved field, and a two-workspace test proving a
foreign reference contributes nothing.
```

---

# Phase 2 — The plan the creator approves

## LF.2.1 `content.long-form-outline` capability

```text
SCOPE: Add the capability that proposes a section plan: a versioned prompt file, an output schema of
ordered sections each with role, heading and one-line intent, an AiTaskType.LongFormOutline member and
AiTaskCatalog registration, a claim scanner pass, and an evaluation set. Grounding is the LF.1.4 package.
The request names the kind, the target length, and optionally the sections the creator already wants.
CONSTRAINT: LFC-003; B-33; .claude/rules/ai.md, content.md and recipes.md; add-ai-capability skill;
LF.1.2–LF.1.4. The prompt file follows B-16 — one embedded .prompt.md per version with a JSON front-matter
manifest and a declared body checksum. Memory: ban the claim frame, not the food word, when writing refusal
fixtures.
RESTRICTION: The outline is a plan, not prose: a section's intent is one line and the schema has nowhere to
put a body. Roles are validated against LF.1.3 after generation; an unknown or repeated single-use role
fails rather than being repaired. It may not invent recipe facts, quantities, times, temperatures,
nutrition, allergen or safety claims, search metrics, or anything about a picture beyond what the package
says. A newsletter plan proposes exactly one item section per resolved source reference and no more. Output
is a proposal until the creator approves it. Prompt bodies and generated content are not logged.
UTILIZATION: add-ai-capability; ai-safety-reviewer after implementation.
BEHAVIOR: Show the schema, the prompt's declared inputs and outputs, and the fixture list; wait for
approval; implement with evaluation fixtures for quality, an unknown role, a repeated single-use role, a
body smuggled into an intent line, a food-safety or allergen claim, a wrong item count for a newsletter,
prompt injection through each untrusted section, missing source, and workspace isolation.
```

## LF.2.2 Outline request and approval endpoints

```text
SCOPE: Add the request seam under /api/v1/workspaces/{workspaceSlug}/long-form-drafts: POST a draft for a
creative context and a kind (creating the draft and requesting an outline, 202 with an operation), GET the
draft with its latest revision, GET status, PATCH the creator's edits to the plan as a new revision, POST
the plan's approval (moving the draft to Outlined), and GET a cursor list of recent drafts.
CONSTRAINT: LFC-001, LFC-003; LF.1.2–LF.2.1; the shared AI lifecycle and 9A allowance admission;
.claude/rules/api-contract.md and backend.md; add-endpoint skill.
RESTRICTION: Create takes an Idempotency-Key that names the request — the same key with a different context
or kind is refused. The context is validated through ICreativeContextFacade in the resolved workspace;
unknown and other-workspace contexts answer the same 422 with a stable code and disclose nothing.
WorkspaceId never comes from the body. Updates use the row version / ETag. An exhausted allowance is refused
at admission, before any provider call. Approving twice does not create two approvals, and approving a plan
that fails LF.1.3 validation is refused with the failing sections named. Contributor and above may write;
Viewer may read.
UTILIZATION: add-endpoint; api-contract-checker and workspace-isolation-auditor after implementation.
BEHAVIOR: Show routes, ViewModels, ServiceModels and error codes; wait for approval; implement the full seam
with tests for idempotent create, a foreign-workspace context, stale ETag, double approval, an invalid plan,
allowance refusal, cancellation marking the outcome accurately, and two-workspace isolation; regenerate the
OpenAPI snapshot with UPDATE_OPENAPI_SNAPSHOT=1 and read the diff.
```

## LF.2.3 Outline review surface

```text
SCOPE: Add the typed web side and the plan-review screen: models in src/web/src/app/models/, a
LongFormDraftService in src/web/src/app/services/, and a feature component that shows the proposed sections
in order with role, heading and intent, lets the creator rename a heading, reorder, remove a section, add
one from the roles still available, and then approve the plan.
CONSTRAINT: LFC-003; B-33, B-37; LF.2.2; .claude/rules/frontend.md and design-system.md; the "easy as pie"
product rule; new-component and creatorpantry-design-system skills.
RESTRICTION: Components do not inject HttpClient. Built from @creator-pantry/ui exports — CpFormSectionComponent,
CpFieldComponent, CpNoticeComponent, CpStatusPillComponent — with no new primitive and no feature stylesheet
redrawing a form heading, a section intro or a field row. Reordering is keyboard-operable and never
pointer-only. The plan is visibly generated and visibly editable before approval, and nothing is approved
automatically. An unsaved edit survives a failed save and navigation, with the standard warning. Every data
surface has loading, empty, degraded, error and success states.
UTILIZATION: new-component; design-review after implementation.
BEHAVIOR: Show the screen states and the plan's edit model, wait for approval, implement with service,
component, contract, accessibility and error-path specs covering keyboard reordering, the unsaved-edit
guard, and an invalid plan refused by the server.
```

---

# Phase 3 — The prose

## LF.3.1 `content.long-form-section` capability

```text
SCOPE: Add the capability that writes one section: a versioned prompt file, an output schema of one body
plus the model's own cautions, an AiTaskType.LongFormSection member and AiTaskCatalog registration, and an
evaluation set. The request names the draft revision, the section, and what the creator wants changed when
regenerating. Grounding is the LF.1.4 package plus the approved plan and the headings and first lines of the
sections already written, so a section follows what came before without repeating it.
CONSTRAINT: LFC-004; B-33, B-36; .claude/rules/ai.md, content.md and recipes.md; add-ai-capability skill;
LF.1.3, LF.2.1. B-16 for the prompt file.
RESTRICTION: It writes one section and has nowhere to put another. It writes prose about the recipe and
never changes it: no quantity, step, yield, time, temperature or safety fact may be altered, and every
figure it writes must appear in the package exactly as it does there — nothing added, converted, rounded,
totalled, scaled or recomputed. With no recipe reference in the package it writes no recipe facts at all. It
may not claim a recipe is safe, healthy, free of any allergen, suitable for any diet, nutritionally anything
or guaranteed to work; it may not invent search metrics; it may not describe a picture beyond what the
package says. Where B-36 says a section's material is already in an accepted editorial package, it is told
to work from that text rather than write it again. Length is enforced by the LF.1.3 validator after
generation and an over-ceiling body is returned flagged, never trimmed. Output is a proposal until the
document is accepted. Prompt bodies and generated content are not logged.
UTILIZATION: add-ai-capability; ai-safety-reviewer after implementation.
BEHAVIOR: Show the schema, the prompt's declared inputs and outputs, the per-role guidance, and the fixture
list; wait for approval; implement with evaluation fixtures for quality per role, an over-ceiling body, a
recomputed figure, a food-safety or allergen claim, an invented search metric, a picture described with no
analysis, a recipe fact written with no recipe reference, prompt injection through each untrusted section,
a section repeating its predecessor, stale source, and workspace isolation.
```

## LF.3.2 Long-form claim scanner

```text
SCOPE: Add the deterministic post-generation scanner for long-form bodies, in the Content module beside the
validator: every number, quantity, time, temperature and yield in a section body is checked against the
pinned sources the revision names, and one not found there is recorded as a warning on the section. Claim
frames the editorial and SEO scanners already catch — safety, allergen, dietary, medical, nutrition,
preservation, search metrics — are carried over.
CONSTRAINT: LFC-004; .claude/rules/ai.md and recipes.md; AiEditorialClaimScanner and AiSeoClaimScanner as
the pattern to extend, not to duplicate.
RESTRICTION: Code, not a model, and it runs after validation on every section whatever its source — a
creator's own edit is scanned too, and its findings are shown, never blocking the save. A warning is a
warning: nothing is rewritten, removed or refused on the scanner's say-so. Server findings are added to the
model's own cautions and cannot be removed by them. Where the editorial scanner already implements a frame,
call it rather than restating the pattern. Memory: ban the claim frame, not the food word.
BEHAVIOR: Show what is scanned, the frame list and where it is shared with the existing scanners; wait for
approval; implement with tests for a figure present in the source, a figure absent from it, a figure in a
section with no recipe reference, each carried-over claim frame, a creator-edited body, and a body whose
findings must not suppress the model's own cautions.
```

## LF.3.3 Section request, status, and settle endpoints

```text
SCOPE: Extend the request seam: POST a section-writing request for one section of a draft (202 with an
operation), POST a request to write every section not yet written (one operation per section, enqueued in
plan order), GET the draft's per-section status, PATCH a creator edit to one section as a new revision, and
POST a section's settled flag.
CONSTRAINT: LFC-004; B-33; LF.2.2, LF.3.1, LF.3.2; the shared AI lifecycle and 9A allowance admission;
.claude/rules/api-contract.md; add-endpoint skill.
RESTRICTION: Admission is held per section, so "write the rest" is refused up front when the allowance
cannot cover every remaining section and the response says how many it could cover — it never half-spends
silently. Each request takes an Idempotency-Key naming the draft revision and the section; the same key for
a different section is refused. Writing one section leaves every other untouched. A cancelled run marks each
section's outcome accurately and leaves the sections already written intact. A section of a draft that is
not Outlined or Writing is refused. Settling a section is reversible and is not acceptance.
UTILIZATION: api-contract-checker and workspace-isolation-auditor after implementation.
BEHAVIOR: Show routes, shapes, the fan-out's ordering and the admission arithmetic; wait for approval;
implement with tests for per-section idempotency, partial allowance refused up front, regeneration of one
section, cancellation mid-fan-out, a section of a draft in the wrong status, un-settling, and two-workspace
isolation; regenerate the OpenAPI snapshot and read the diff.
```

## LF.3.4 Assembled document, review, and acceptance

```text
SCOPE: Add the whole-document seam and screen: a deterministic assembler in the Content module that renders
the revision's sections in plan order into one Markdown document with the creator's own headings, the
document's word count and ceiling status, and its warnings gathered per section; POST the document's
acceptance; and the feature screen where the creator reads the article whole, jumps to a section, edits it,
regenerates it, settles it, and accepts the document once.
CONSTRAINT: LFC-005; B-33, B-37; LF.3.3; .claude/rules/content.md, frontend.md and design-system.md; the
"easy as pie" rule; add-content-feature, new-component and creatorpantry-design-system skills. Reuse the
AF.6.5 per-item review composition and RecipeMarkdownEscaper's escaping conventions.
RESTRICTION: The assembler is a projection: same revision in, byte-identical document out, no timestamps,
and it writes nothing. No "(editorial)" labelling — this document is the creator's article, not a review
scaffold — but prose reused from an accepted editorial package is attributed in the UI, per B-36. Acceptance
is of the document and accepting twice does not create two accepted revisions. A draft with an unwritten
section or a section over its ceiling may be accepted only after an explicit confirmation naming what is
wrong. Generated content is visibly identified and editable before acceptance. An unsaved edit survives a
failed save, a regeneration of another section, and navigation, with the standard warning. Warning status is
conveyed by more than colour. Edits are plain Markdown in a textarea inside cp-field; no rich-text editor.
Nothing here publishes, schedules or exports.
UTILIZATION: add-content-feature, new-component; design-review and test-gap-analyzer after implementation.
BEHAVIOR: Show the assembler's output contract, the screen's states and the per-section state machine; wait
for approval; implement with golden-file tests for a complete, a minimal, a reused-editorial and a
special-character document, and with component, contract, accessibility and error-path specs covering the
unsaved-edit guard, acceptance confirmation, and a long document at 200% zoom.
```

---

# Phase 4 — The newsletter issue

*If the owner corrects B-35 at LF.1.1 to say an issue is always about one recipe, cut this phase and add
`NewsletterIssue` as a kind of Phase 3 instead.*

## LF.4.1 Multi-source issue shape

```text
SCOPE: Make a newsletter issue's multi-source shape real: the issue's item sections are bound one-to-one to
the creative context's ordered source references, the plan validator enforces that binding, and reordering
the issue's items reorders by reference rather than by free position. Add the creator's issue-level framing
— a subject line and a short note — to the draft.
CONSTRAINT: LFC-006; B-35; LF.1.2, LF.1.3, LF.2.2; add-content-feature skill; .claude/rules/content.md.
RESTRICTION: An item whose reference no longer resolves is kept, marked unresolved, and reported — a
revision pinned it and an immutable revision is not edited by a later deletion. The subject line is the
creator's; nothing generates it in this prompt. No send, no list, no subscriber, no provider.
BEHAVIOR: Show the binding rule and what an unresolved item reads as; wait for approval; implement with
tests for one, two and five references, a reference removed from the context after a revision pinned it,
reordering, and an item count that disagrees with the references.
```

## LF.4.2 Newsletter writing and review

```text
SCOPE: Teach LF.2.1 and LF.3.1 the newsletter roles and bind each item generation to its own source
reference, so an item is written from that recipe and no other. Extend the LF.3.4 screen to show an issue as
its subject, framing and ordered items.
CONSTRAINT: LFC-006; LF.3.1, LF.3.4, LF.4.1; add-ai-capability skill. Bump both prompt versions and update
both evaluation sets, since behaviour changes.
RESTRICTION: An item names facts from its own reference only; a fact belonging to another item's recipe in
this same issue is a cross-contamination failure and gets a fixture of its own. The word target is the
issue's, not each item's. Nothing about sending appears on the screen.
UTILIZATION: ai-safety-reviewer after implementation.
BEHAVIOR: Show the per-item grounding slice and the two prompt diffs; wait for approval; implement with
evaluation fixtures for a two-recipe and a five-recipe issue, an item citing another item's recipe, an
unresolved item, and the issue-level word target.
```

---

# Phase 5 — Export and the SEO package

## LF.5.1 Long-form Markdown and HTML export

```text
SCOPE: Add deterministic Markdown and HTML export of an accepted long-form draft — the creator's headings,
the sections in plan order, the subject and framing for an issue, and an attribution block naming the
recipe versions and the draft revision the document came from.
CONSTRAINT: LFC-007; .claude/rules/content.md; add-content-feature skill; RecipeMarkdownExporter and
RecipeMarkdownEscaper for conventions.
RESTRICTION: A projection, not a stored competing document: it reads, formats and writes nothing, and the
same input gives byte-identical output with LF newlines and no timestamps. Every creator string is escaped;
the HTML export escapes and does not pass Markdown HTML through. The recipe Markdown export's bytes do not
move — assert it. Exporting an unaccepted draft is refused. No external image URL is fetched and no asset
bytes are embedded.
BEHAVIOR: Show both templates and the attribution block; wait for approval; implement with golden-file tests
for a complete article, a minimal one, an issue, special characters, and a Markdown-injection attempt in a
heading, plus an assertion that the recipe export is unchanged.
```

## LF.5.2 Export endpoints

```text
SCOPE: Add GET .../long-form-drafts/{draftId}/exports/markdown and .../exports/html for an accepted draft,
and a corresponding web download action on the LF.3.4 screen.
CONSTRAINT: LFC-007; LF.5.1; add-endpoint skill; .claude/rules/api-contract.md and gateway.md.
RESTRICTION: Correct content types, a stable content-disposition filename derived from the creator's title,
no raw storage path, and no unaccepted draft without an explicit policy. Personalised responses are not
cached at a shared proxy layer.
UTILIZATION: api-contract-checker and workspace-isolation-auditor after implementation.
BEHAVIOR: Show parameters, headers, cache and error contract; wait for approval; implement with tests for
both formats, an unaccepted draft, a filename from a title with awkward characters, and two-workspace
isolation; regenerate the OpenAPI snapshot.
```

## LF.5.3 SEO package for a long-form draft

```text
SCOPE: Extend the existing content.seo-package capability and its seam to take an accepted long-form draft
as its subject as well as a recipe, so the article gets a title, slug, meta description, key phrases and
internal-link ideas written against the article's own text. Surface it on the LF.3.4 screen.
CONSTRAINT: LFC-007; master 11.5 as built; .claude/rules/ai.md and content.md; add-ai-capability skill.
Bump the prompt version and update the evaluation set.
RESTRICTION: Do not fork a second SEO capability, schema or validator — extend the one that exists, and
leave its behaviour for a recipe subject unchanged. It must not invent search volume, ranking or competitor
data, and it must not describe an image it has not been given facts about. SEO output is a recommendation,
never a guarantee. The article is untrusted content to this prompt as any source text is.
UTILIZATION: ai-safety-reviewer and api-contract-checker after implementation.
BEHAVIOR: Show the subject-selection change, the prompt diff and the fixture additions; wait for approval;
implement with fixtures for an article subject, an unchanged recipe subject, an invented metric, slug
determinism, and injection through the article body; regenerate the OpenAPI snapshot.
```

---

# Phase 6 — Staleness and the surfaces

## LF.6.1 Long-form staleness

```text
SCOPE: Extend master 11.3's staleness propagation to long-form drafts: when a pinned recipe version, brand
style guide version or prompt template moves, an accepted draft becomes NeedsReview with its stale reasons
recorded, through the existing outbox or durable job after commit, and the screen says which pin moved.
CONSTRAINT: LFC-008; LF.1.2, LF.3.4; IContentStalenessFacade as built; add-content-feature and
add-background-job skills; .claude/rules/content.md ("mark NeedsReview; do not rewrite automatically").
RESTRICTION: The source write and the durable event row commit together. Nothing is rewritten, regenerated
or deleted — an accepted revision stays exactly as accepted. The consumer is idempotent and replay-safe. A
draft that was never accepted does not go stale. Per-section staleness is not modelled: the document is the
unit, as acceptance is.
UTILIZATION: workspace-isolation-auditor and test-gap-analyzer after implementation.
BEHAVIOR: Show the event, the transaction and the stale-reason mapping; wait for approval; implement with
commit, failure, replay and two-workspace tests, and prove a recipe update cannot leave an accepted article
falsely current.
```

## LF.6.2 Content Pipeline article step

```text
SCOPE: Add a seventh pipeline step, "Write the post", between posts and library: it runs the LF.2–LF.3
journey inside the pipeline shell for the run's creative context, and the posts step's teasers then name the
article they are teasers for.
CONSTRAINT: LFC-009; AF.6.5 and the pipeline shell's step list in content-pipeline.models.ts; the "easy as
pie" rule; .claude/rules/frontend.md and design-system.md; new-component and creatorpantry-design-system
skills.
RESTRICTION: Reuse the LF.2.3 and LF.3.4 components; do not fork a second long-form surface for the
pipeline. The step's progress figure stays the truth about the journey — adding a step changes "of 6" to
"of 7" everywhere it is computed, including the Workflows Hub. A long-form draft is optional: a creator who
only wants posts skips the step and the run still completes. State kept for the step lives on the server
with the draft, not in localStorage. Nothing is accepted or published for the creator.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the step's placement, help and legend text, the skip path and every place the step count is
computed; wait for approval; implement with component, contract, accessibility and resume specs, including
a run that skips the step.
```

## LF.6.3 Workflows Hub entry

```text
SCOPE: Add "Write a blog post or newsletter" to the Workflows Hub as its own entry, starting from a recipe
or from a blank brief, and reconcile it with master 13A.18 — which stays the wizard shell and now composes
LF.2–LF.5 rather than specifying a writing step of its own.
CONSTRAINT: LFC-009; master 13A.18 and Phase 11A's shell; workflows-hub.component.ts; the "easy as pie"
rule; new-component skill.
RESTRICTION: Do not fork a second guide wizard and do not activate anything implicitly. The entry offers
only the destinations that exist; an unbuilt one is not offered. Annotate 13A.18 in the master library
rather than rewriting it.
UTILIZATION: design-review after implementation.
BEHAVIOR: Show the entry, its starting points and the 13A.18 annotation; wait for approval; implement with
deep-link, resume and error specs.
```

---

# Phase 7 — Proving it

## LF.7.1 Long-form end-to-end test

```text
SCOPE: Add one HTTP-level test in the style of CreativePipelineEndToEndTests that runs the whole journey for
Workspace A — recipe, accepted editorial package, creative context from that recipe, long-form draft,
outline, plan edit and approval, every section written, one regenerated, one edited, document accepted, SEO
package, Markdown and HTML export, then a recipe edit that marks the draft NeedsReview — while Workspace B
attempts every step against A's ids and reaches none of them. Add a second pass for a two-recipe newsletter
issue and a third for a draft with no recipe reference at all, proving it carries no recipe facts.
CONSTRAINT: Every earlier phase; .claude/rules/tenancy.md (Required tests). Memory: SQL container contention
can red a class in a full run — re-run it alone before believing a failure.
RESTRICTION: A test, not a new feature — where a step cannot be driven over HTTP, report the gap instead of
reaching into a repository. The model provider is the test double the existing suite uses.
UTILIZATION: test-gap-analyzer after implementation.
BEHAVIOR: Show the step list and what B attempts at each; wait for approval; implement; run the class alone
with dotnet run --project src/CreatorPantry.Tests -- --filter-class and then the full suite; report.
```

## LF.7.2 Long-form audit

```text
SCOPE: Audit, do not build. Run the read-only reviewers over everything this document delivered, then walk
the feedback in the running app: draft a full blog post from a recipe, a newsletter issue from two recipes,
and an article from a brief with no recipe, recording for each whether a creator can do it without retyping
anything and what the finished length actually was.
CONSTRAINT: aspire run; dotnet run --project src/CreatorPantry.Tests; npm run build and npm test from
src/web/.
RESTRICTION: Fix blocker-level findings only when they are small and local; anything larger becomes a
written follow-up prompt appended to this file. Do not mark a requirement resolved from tests alone. Report
the real word counts against B-34's targets and say plainly whether the numbers need revising.
UTILIZATION: architecture-reviewer, workspace-isolation-auditor, ai-safety-reviewer, api-contract-checker,
design-review, test-gap-analyzer, skills-evals; playwright-cli for the walk-through.
BEHAVIOR: Report per requirement (resolved / partly / not), per reviewer (findings by severity), the
measured lengths, and the follow-up prompts written. Update the checkpoint table and the `- done` markers.
```

---

## Definition of done for one LF prompt

- The stated seam is complete from controller to repository or gateway, with no layer skipped.
- Its tests pass, including a two-workspace isolation test for every workspace-scoped behaviour.
- An AI prompt change bumps the version, updates the body checksum, and runs the evaluation set.
- An API shape change regenerates `src/CreatorPantry.Tests/Api/Snapshots/openapi-v1.json` and the diff has
  been read.
- A migration has been reviewed, applied, and `aspire run` restarted so `MigrationService` picked it up.
- Public UI changes update the showcase, `README.md`, `DESIGN-SYSTEM.md`, and `public-api.ts` as appropriate.
- The prompt is marked `- done` with a delivery note wherever what shipped differs from what was asked.
