# Food-Domain Safety Audit

*Status: Accepted — 2026-10-20. Recorded by microprompt 14.7 ("Food-domain safety audit").*

An audit of substitution, adaptation, AI quality review, readiness, nutrition, allergen, dietary and
high-risk-method behaviour, across five dimensions: evidence, uncertainty, wording, deterministic routing and
source licences. This is a status snapshot, the same kind `docs/ai-evaluation-baseline.md` keeps for the AI
foundation; the decisions it produced are recorded as **B-43** in `docs/architecture-decisions/baseline.md`.

The requirement ids this task cites — `AIREC-GR-005`, `AI-009`, `AI-010`, `ING-007`, `ING-008` — are not
defined anywhere in this repository; only the ids appear, in the microprompt list. As
[B-12](architecture-decisions/baseline.md#b-12-unmapped-dec-items--decide) records for the rest of that family,
the operative spec is `.claude/rules/{ai,recipes}.md` plus the microprompt's own SCOPE and RESTRICTION, and the
baselines those earlier prompts recorded (B-33 through B-42).

## Method

Four read-only reviewers ran in parallel, one per dimension: `ai-safety-reviewer` (wording, uncertainty, the
AI/deterministic boundary), `architecture-reviewer` (deterministic routing and layer boundaries),
`test-gap-analyzer` (what safety behaviour no test pins), `integration-reviewer` (sources, licences,
provenance). Every blocker and high-severity finding was then verified against the files before being accepted,
and three claims did not survive that check — they are recorded under [Not findings](#not-findings) rather than
dropped, because the reasoning is worth keeping.

## What the audit found

The deterministic side held up. Allergen, dietary, nutrition and food-safety *determinations* are model-free,
record-backed and correctly worded; no model can alter one, no client re-derives one, and the three analyzers
are pure. Everything below sits on the AI advisory capabilities, on reach, or on test pinning.

### Blockers

| # | Finding | Resolution |
| --- | --- | --- |
| BL-1 | `recipe.review-1.0.0` **instructed the model** to say a recipe reads "safe as written" when it found nothing, and the validator accepted any general warning, so a model-written all-clear reached the creator on a capability whose own safety class is `FoodSafety`. | Prompt `recipe.review-1.1.0` asks for what was examined and forbids the verdict; the empty-answer warning is replaced with a fixed server sentence (`RecipeReviewAiTaskHandler.EmptyAnswerMessage`) so the wording cannot drift back with the next model. |
| BL-2 | Free-text safety and free-from claims were unscanned in substitution, review, adaptation, revision and first-draft output. Four claim scanners existed; none was wired to a recipe capability. The suite **pinned** `"Gluten-Free Buttermilk Soda Bread"` as a valid proposed title. | `AiRecipeSafetyClaimScanner` refuses an assurance in any creator-facing field, wired into all five validators; the pinned fixture and test now pin the refusal. |

### High

| # | Finding | Resolution |
| --- | --- | --- |
| H-1 | A dietary adaptation could change the recipe with no safety caution — the rule substitution and review both enforce. | `CautionedDietaryAdaptation` in `AiAdaptationOutputValidator`. |
| H-2 | A yield-goal adaptation could set `yieldQuantity`, `servingCount` and `servingSize` itself; only ingredient quantities were checked against deterministic scaling. A model-chosen servings figure becomes the divisor for every per-serving nutrition figure. | `RecipeYieldIsDeterministic`: the yield must equal the computed figure, and servings fields are refused under that goal. |
| H-3 | **14.5's warnings reached no creator.** No endpoint, no readiness rule, no screen — a recipe flagged for canning could be approved and published with nothing said. | `GET .../analysis/food-safety`, and the recipe editor's **Safety checks** tool. |
| H-4 | A nutrition estimate reported `Complete` and `publishable` while silently omitting an unquantified required line, and was vacuously complete with nothing counted at all. | An unquantified non-optional line is required and unresolved; publishability needs something counted. **Amends B-35** — see B-43. |
| H-5 | Readiness showed the allergen rule **Satisfied** when no line matched an ingredient, so absence of evidence read as a pass. | `NotApplicable`, with its own sentence; the facade reports whether the read happened. |
| H-6 | The review's reference-check floor covered only allergen/dietary categories or major+ severity, and the model chooses the severity — so a "moderate" internal-temperature finding shipped as model knowledge with no reference flag and no caution. | Temperature and timing are in the floor at any severity. |
| H-7 | No rule required an `Approved` trait to cite a catalogue-approved source; only the absence of a write path kept B-33 true. | `TraitPolicy.MayBeApproved`, with tests asserting no source may be approved for allergen, dietary or food-safety evidence. |
| H-8 | `High` confidence on the model's own knowledge was accepted — and pinned — for allergen-bearing swaps. | Refused for an allergen or dietary consequence; still allowed for ordinary culinary guidance. |
| H-9 | Unpinned detector alternatives: "sodium nitrite" appeared only in negatives, and ceviche, sashimi, salmonella, sun-drying and the canning equipment were pinned nowhere. Deleting a pattern would have dropped a warning with every test still green. | Every alternative pinned. Four real gaps surfaced doing it ("prevents salmonella", "no botulism risk", "no chance of food poisoning", "good for gestational diabetes") and the patterns were widened. |

### Medium and low, by theme

- **Enforcement coverage.** The evaluation harness checked each category across *all* capabilities at once, so
  substitution, adaptation and review having no `WorkspaceIsolation` fixture was invisible; there is now a
  per-capability check over the four food-advising capabilities, and the four missing fixtures exist.
  `ModuleBoundaryTests` admitted every `{Module}.Managers` namespace, so the three analyzers could be called
  across a boundary, skipping the facade that resolves the workspace — they are named as facade-only now. The
  analyzers' purity is enforced by `SafetyAnalyzerPurityTests` rather than promised in prose.
- **Wording.** A mapping's match was labelled "High/Low confidence" when it records how the candidate was found
  ("Name matched exactly", "Several possible matches" now); the model's self-rated confidence and basis are
  labelled as the model's own; an incomplete nutrient's figure carries "at least"; nutrients no counted
  reference reports are named rather than silently absent; the dietary panel says it reads ingredient lines only.
  The dietary panel's "about the records, not likelihood" caveat is now shown on *every* finding — it used to be
  hidden exactly where the answer reads most like reassurance.
- **Guards.** The analyzer's "no safe member" guard named five enums by hand and missed three; it is reflective
  now, over every enum in both modules. The food-safety message guard gained the words it lacked, plus an enum
  guard of its own. The two UI copy guards scanned TypeScript only — `scripts/check-safety-copy.mjs` now scans
  both halves of all three safety panels, with its own tests and a denial list that cannot outlive its sentences.
- **One rule, one place.** A cited word that does not count now says *why* — `TraitCountedReason`, with
  `SourceDoesNotSay`, `UnvettedKind` and `RetiredSource` told apart. The dietary panel had been working that out
  from the source's kind and its active flag, which was a copy of the domain's `MayReassure` living in a
  browser: correct the day it was written and wrong the day the rule changes.
- **Provenance and licence.** The licence is no longer null-forgiven into a model the UI binds to an `href`; a
  source with none recorded says so instead of the line vanishing; USDA's citation text was refreshed to their
  current wording.
- **Determinism.** The analyzer now filters traits by the evaluation date it was already given — it recorded the
  date and compared nothing with it, so any second read path would have cited records not yet in effect. `Trace`
  reads both bounds of a range. Division by zero is refused rather than thrown.
- **Isolation.** Reject and remap are pinned at the HTTP layer, not confirm alone.

### Not findings

Three reported items did not survive verification, and the reasoning is the useful part:

1. **A retired or community source moving a cautious finding is correct.** The brief given to one reviewer said
   such a source "can never move a finding, only be cited"; B-36 says only that a *reassuring* word needs a
   vetted, active source — "any approved source may give a cautious one". A community-sourced `Present` counting
   as `Contains` is the conservative direction, and the code follows the baseline. The brief was wrong.
2. **`toFixed(displayPrecision)` is not client arithmetic.** The server rounds to that precision, so it pads
   trailing zeros and cannot change a value. Two reviewers checked independently.
3. **The two adaptation findings are not blockers**, because acceptance applies a creator-selected
   `acceptedChanges` subset. They would become blockers if an automation path ever applied proposals unselected.

Two patterns I wrote during the fixes were themselves too blunt and were corrected by their own tests: a
`cures?` pattern that flagged "cure the pork belly", and a guard that forbade the word "exactly" — which this
product legitimately uses, both for the server's unrounded figures and for a name that matched exactly.

## The reviewed source and version matrix

### Evidence sources

| Source | Code | Kind | Approved for | Data sets | Licence | Status |
| --- | --- | --- | --- | --- | --- | --- |
| USDA FoodData Central | `usda-fdc` | `OfficialDatabase` | Nutrition, Density | `foundation`, `sr-legacy` | CC0-1.0 | Approved (B-09, B-33). Off by default: `Usda:Evidence=false`, key sent only as `X-Api-Key`. |
| — | — | — | **Allergen** | — | — | **No approved source.** Findings read as *Unknown*; that is the correct answer for records that do not exist. |
| — | — | — | **Dietary** | — | — | **No approved source.** |
| — | — | — | **Food safety** | — | — | **No approved source.** Every warning carries `VerificationRequired`. |

Not approved from FDC: Branded (manufacturer label data), Survey/FNDDS (prepared dishes), Experimental
(research data). Rejected at the reader, not merely unrequested. Verified against fdc.nal.usda.gov on
2026-10-20: CC0 1.0, attribution requested, no endorsement clause.

### Deterministic method versions

| Version | Owner | Decides |
| --- | --- | --- |
| `dietary-allergen/1` | `DietaryAllergenAnalyzer` | Allergen and dietary findings, their coverage and confidence |
| `nutrition-estimate/1` | `NutritionEstimateCalculator` | Every nutrition figure, its rounding and its completeness |
| `food-safety-warnings/1` | `FoodSafetyWarningDetector` | High-risk method and claim warnings |
| `1.1.0` | `RecipeReadinessCatalogue` | Readiness verdicts, including the allergen-records rule |

None changed in this audit: the fixes added rules and corrected classification without altering a published
figure or finding. The nutrition change alters what `publishable` *means*, which is recorded as an amendment to
B-35 rather than a new method version — the arithmetic is untouched, and both goldens show only the
reclassified line and the new group.

### Prompt templates in use

The store resolves the highest version per id, so these are what runs.

| Template | Version | Safety class | Claim scanning |
| --- | --- | --- | --- |
| `recipe.review` | **1.1.0** (was 1.0.0) | `FoodSafety` | Findings, warnings |
| `recipe.substitution` | 1.0.0 | `DietaryOrAllergen` | Every prose field, warnings |
| `recipe.adaptation` | 1.0.0 | `DietaryOrAllergen` | Proposed values, warnings |
| `recipe.revision` | 1.0.0 | — | Proposed values, warnings (base validator) |
| `recipe.first-draft` | 1.3.0 | — | Every creator-facing field |
| `recipe.concepts` | 1.2.0 | — | Not change-shaped; unchanged by this audit |

### Evaluation coverage, after the audit

515 fixtures across seven categories. The four food-advising capabilities each now carry `RefusalSafety`,
`SchemaValidity`, `WorkspaceIsolation` and `PromptInjection` fixtures, enforced per capability rather than
globally.

## What this audit did not do

- **No source was approved.** Closing a finding by approving FSIS, NCHFP or any other reference would be the
  fabrication this task's RESTRICTION forbids; approving one is its own decision, taken after reading its terms,
  recorded in the baseline.
- **No warning was weakened.** Every pattern change widened what is caught. Where a check was switched from
  blunt to precise — `Calculator` as a forbidden suffix, a bare `cures?` — the replacement was narrower in
  wording and no narrower in what it refuses.
- **Nutrition still does not gate approval.** `publishable` is exposed and unread, as B-39 left it. Wiring it to
  readiness would make an unmapped ingredient block an approval, which is a product decision nobody has taken.
- **`Manufacturer` may still assert an allergen absence.** `TraitPolicy.MayAssertAllergenAbsence` admits it, and
  B-33 excludes branded label data as evidence — a narrowing that changes a database check constraint and so
  needs a migration of its own. No manufacturer source is approved, so nothing reaches it today.
- **A mapping's match confidence is still absent from an estimate line**, so a counted figure resting on a
  low-confidence reference is reachable only by opening that mapping. Carrying it on the line is a server change.
