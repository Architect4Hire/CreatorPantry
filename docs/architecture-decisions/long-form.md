# Long-Form Content Decisions

*Status: Accepted — 2026-10-12. Recorded by long-form prompt LF.1.1.*

This record fixes the product decisions behind the long-form writer — a full blog article and a full
newsletter issue, written from a recipe, from the creator's own brief, or from a saved prompt — so the LF
prompts build on a stated baseline instead of an assumption. It does not implement code, and it records
nothing the LF prompt document does not state.

Identifiers continue the `B-` sequence from [`ai-fluency.md`](ai-fluency.md), which ends at B-31. Do not
renumber them; later documents may reference them.

## Sources

- `docs/prompts/creatorpantry-long-form-scrub-prompts.md` — the LF prompts, the requirement table, and the
  decisions recorded here.
- Creator feedback received 2026-10-10, tracked as LFC-001 through LFC-009 in that document: *the blog type
  for the content pipeline only writes up to 600 characters — there is no way to draft a full newsletter or
  blog post.*
- Product-owner answers given 2026-10-12 — B-32 through B-37 confirmed as the LF document states them, and
  the four B-34 figures approved as proposed.
- [`ai-fluency.md`](ai-fluency.md) — B-27, which this record leaves standing, and B-31, the `CreativeContext`
  a long-form draft is rooted on.
- `docs/prompts/creatorpantry-scrub-microprompts.md` — the master library whose prompts are extended or
  narrowed.
- `CLAUDE.md` and `.claude/rules/` — architectural source of truth. Nothing here redefines them.

## Decision table

| ID | Topic | Requirement | Status | Decision |
| --- | --- | --- | --- | --- |
| B-32 | Long-form is its own artifact | LFC-001 | Decided | A `LongFormDraft` rooted on a `CreativeContext`. Not a channel post, and not a new `ContentPackageKind`. |
| B-33 | Outline first, then one section at a time | LFC-003, 004, 005 | Decided | The creator approves a section plan before any prose exists; each section is its own generation; acceptance is of the document. |
| B-34 | Length is an editorial target | LFC-004 | Decided | New editorial constants: hard character ceilings enforced in code, target word ranges as guidance. Channel profiles are untouched. |
| B-35 | A newsletter issue may cover more than one source | LFC-006 | Decided | An issue takes an ordered set of the context's references, one `item` section each, plus the creator's own framing. |
| B-36 | Accepted editorial and SEO packages are inputs | LFC-002 | Decided | Long-form reuses and attributes accepted editorial prose, and never writes back to a `ContentProposal`. |
| B-37 | Long-form text is Markdown in a plain control | LFC-005, 009 | Decided | A `textarea` inside `CpFieldComponent`. No rich-text editor and no new frontend dependency. |

## Decisions

### B-32 Long-form is its own artifact, rooted on a creative context

- **Long-form is not a channel post, and not a new `ContentPackageKind`.**
- **A `LongFormDraft` hangs off a `CreativeContext`**, the way `SocialPackage` does.
- **It can be started from a recipe, from the creator's own brief, or from a saved prompt.** The brief is the
  context's `WorkingBrief` and the prompt is its `PromptRecord` reference kind; neither is invented here.
- **B-27 stands untouched.** `blog` stays a 600-character intro and `newsletter` a 400-character blurb, and
  they become the teasers that point at the long-form piece.

**Options considered:**

- *Raise the `blog` ceiling* — rejected. The channel profile's output kind, counting rule, and prompt all say
  "intro". A 5,000-character `BlogIntro` would be a long teaser sitting inside a post package, with no
  section structure, no outline, no per-section regeneration, and no link to an SEO package.
- *A new `ContentPackageKind`* — rejected. `ContentProposal` requires a `RecipeId`, allows exactly one
  proposal per recipe and kind, and `ContentRevision` repeats `RecipeId` so its recipe-version pin can be
  constrained by a composite key. Long-form has to work with no recipe at all, and a creator writes more than
  one newsletter about the same recipe. Making `RecipeId` nullable would weaken a shipped invariant on
  editorial and SEO to serve a different artifact.

**Consequences:** the deferral of long-form to `content.editorial-package` (master 11.4) recorded under
[B-27](ai-fluency.md) is replaced by this record; 11.4 writes an article's parts and never an article. The
entity, its indexes, delete behaviours and migration are designed and approved in LF.1.2.
**Rules:** `content.md`, `tenancy.md`, `backend.md`.

### B-33 Outline first, then one section at a time

- **A long article in one model call is neither reviewable nor reliable.** The lifecycle is:

  ```text
  Draft → Outlined → Writing → Assembled → Accepted
                                        └→ NeedsReview (a pinned source moved)
  ```

- **The creator approves or edits a section plan before any prose exists.**
- **Each section is a generation of its own**, regenerable without touching its neighbours.
- **"Write the rest" is a convenience** that enqueues one operation per remaining section, in order.
- **Acceptance is of the document, not of a section.** One accepted revision is the article of record.
- **A section carries a creator-set *settled* flag** so the UI can show progress. It is a working state and
  not a second acceptance semantics.

**Consequences:** an article is a dozen generations where a post package is one, so token cost is materially
higher than any existing capability. Admission is held per section (LF.3.2), and the creator is shown what
"write the rest" will spend before it spends it (LF.3.3).
**Rules:** `ai.md`, `content.md`.

### B-34 Length is an editorial target; channel profiles are untouched

- **`ContentChannelProfiles.cs` is not edited by the LF work.** Long-form ceilings are new editorial
  constants.
- **Hard ceilings are enforced in code after generation and never trimmed silently:** 3,000 characters per
  section and 20,000 per document.
- **Target word ranges are passed to the prompt as guidance:** 900–1,600 words for a blog article and
  300–700 for a newsletter issue. A missed range is reported as a warning rather than a failure.

**Confirmed 2026-10-12:** the four figures were proposals in the LF document and were approved as proposed.
See [Open items](#open-items).
**Rules:** `content.md`, `ai.md`.

### B-35 A newsletter issue may cover more than one source

- **A food newsletter is usually a roundup:** two or three recipes, a note from the creator, a link out.
- **A newsletter issue takes an ordered set of the context's references**, one `item` section each, plus the
  creator's own framing.

**Confirmed 2026-10-12.** The LF document named this the assumption most likely to be wrong: had an issue
always been about one recipe, Phase 4 would have collapsed into a mode of Phase 3 and been cut. It was
confirmed as written, so Phase 4 stands.
**Rules:** `content.md`.

### B-36 Accepted editorial and SEO packages are inputs, not competitors

- **When a referenced recipe already has an accepted editorial revision, the long-form draft is given it**
  and told which sections are already written.
- **It reuses that prose and attributes it in the assembled document.** It does not regenerate the same
  material under a new heading.
- **Long-form never writes back to a `ContentProposal`.**

**Consequences:** master 11.4's seven sections remain as they are and become an input to long-form grounding
(LF.1.4).
**Rules:** `content.md`, `ai.md`.

### B-37 Long-form text is Markdown in a plain control; no rich-text editor

- **Sections are edited as plain Markdown in a `textarea` inside `CpFieldComponent`.**
- **No WYSIWYG, no contenteditable, no new frontend dependency.**
- **The design system has no rich-text primitive and the LF work does not add one.**

**Rules:** `frontend.md`, `design-system.md`.

## Library reconciliation

The effect of the LF prompt document on the existing libraries.

| Prompt | Effect |
| --- | --- |
| AF document, "Out of bounds" | **Amended by LF.1.1.** Long-form is no longer deferred to master 11.4; it is the LF document. |
| B-27 (`ai-fluency.md`) | **Unchanged and reaffirmed.** `blog`/`newsletter` stay short-form. LF.1.1 adds a pointer saying where long-form went. |
| master 11.4 Editorial package AI task | **Unchanged.** Its seven sections remain; LF.1.4 and B-36 make an accepted editorial package an input to long-form. |
| master 11.5 SEO package AI task | **Extended by LF.5.3** to take a long-form draft as its subject as well as a recipe. No change to its validators or claim rules. |
| master 11.7 Markdown export | **Unchanged.** LF.5.1 adds a separate long-form export; the recipe export's bytes must not move. |
| master 11.3 Derivative staleness propagation | **Extended by LF.6.1** to long-form drafts. |
| master 13.3 Social Studio UI | Unchanged. |
| master 13A.18 "Write a blog post and SEO package" | **Narrowed.** It stays the wizard shell; the writing steps it composes are LF.2–LF.5. |
| AF.6.1–AF.6.6 | Unchanged. The posts step gains a teaser-for-the-article relationship in LF.6.2. |

11.3, 11.4, 11.5, 11.7 and 13A.18 each carry a one-line pointer in the master file. The prompts themselves are
kept there unedited.

## Open items

### B-34 The four length figures — DECIDED

- **Question:** what are the hard ceilings and target ranges for long-form?
- **Answer:** as proposed, approved 2026-10-12: 3,000 characters per section, 20,000 characters per document,
  900–1,600 words for a blog article, 300–700 words for a newsletter issue.
- **Default assumed by later prompts:** those four figures, as new editorial constants set in LF.1.3.
