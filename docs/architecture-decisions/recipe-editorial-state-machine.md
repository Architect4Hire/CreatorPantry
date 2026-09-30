# Recipe editorial state is a machine (TESTRUN-005)

`Recipe.Status` is a state machine, not a set of labels. `RecipeStatusTransitions` owns the moves; the
transition seam is the only thing that changes the column; every move is recorded in an immutable
`RecipeStatusTransition`.

## The states

`RecipeStatus` gained three members and renamed one. Stored values are unchanged for the three that shipped,
so existing rows keep their meaning.

| Stored | Member | Note |
|---|---|---|
| 0 | `Draft` | unchanged, still the default |
| 1 | `Approved` | renamed from `Ready` |
| 2 | `Archived` | unchanged |
| 3 | `InDevelopment` | new |
| 4 | `Testing` | new |
| 5 | `ReadyForReview` | new |

`Ready` and `Approved` meant the same thing — "the creator considers this finished" — so keeping both would
have left a terminal state permanently reachable outside the machine. The rename is breaking on the wire,
because the enum serializes by name: `recipe.models.ts`, the OpenAPI snapshot and `SettableRecipeStatusViewModel`
moved with it.

## The moves

| From | To | Minimum role | Gate |
|---|---|---|---|
| `Draft` | `InDevelopment` | Contributor | — |
| `InDevelopment` | `Testing` | Contributor | — |
| `Testing` | `ReadyForReview` | Contributor | — |
| `ReadyForReview` | `Approved` | Editor | fresh readiness evaluation, zero blockers; writes a version |
| `InDevelopment`, `Testing`, `ReadyForReview`, `Approved` | `InDevelopment` | Editor | reason required |
| any except `Archived` | `Archived` | Editor | — |
| `Archived` | `Draft` | Editor | — |

Forward one step at a time, so `Draft → Testing` and `Testing → Approved` fail as invalid jumps. Reopening goes
straight to `InDevelopment` from wherever the recipe had got to, rather than walking back a state at a time
through a moment the recipe was never really in. A reopen is the one move that requires a reason, because it is
the one a later reader cannot reconstruct from the two states alone.

Asking for the state the recipe is already in succeeds and writes nothing. That is a repeat rather than a jump,
it preserves REC-006's documented archive idempotency, and it is what keeps a replayed approval from minting a
second version.

## Archive and restore

REC-006's archive and restore predate this and moved the status directly. They now run through the same rules
and write the same history, so a recipe's editorial life reads as one sequence. Their routes, role bar and
answers are unchanged; what changed is the Business method underneath them.

## The approval

The one gated move, and the only write that mints a `RecipeVersionReadiness.Ready` version. It captures a
`RecipeVersion` with source `ReadinessApproval`, so the approval names content that cannot then change
underneath it, and the transition row records `ReadinessRuleSetVersion`, `ReadinessEvaluatedVersionId` and
`CreatedVersionId` — "there were no blockers" is meaningless without which rules were asked.

The readiness evaluation is the **server's own**: `RecipeStatusTransitionFacade` makes it when the target needs
one, and Business refuses an evaluation whose concurrency token no longer matches the recipe. A caller cannot
hand in a verdict, and an evaluation of content that has since changed is not a fresh evaluation whatever it
says.

The facade is separate from `IRecipeFacade` for the reason `IRecipeReadinessFacade` is: `AiProposalBusiness`
already depends on `IRecipeFacade`, so a method there reaching `IAiProposalFacade` through the readiness facade
would close a constructor cycle and fail at container resolution rather than at review.

## What no longer moves the status

Three paths used to, and each is closed:

- **An edit.** `SettableRecipeStatusViewModel` accepts only `Draft`; Business refuses a submitted status that
  differs from the recipe's own. The other two members stay in the schema so a request naming one gets a field
  error rather than an unreadable-body 400.
- **A version restore.** `RecipeSnapshotReconciler` no longer applies `Recipe.Status`. It did, and that would
  now move a recipe to `Approved` — or `Testing`, or `ReadyForReview` — with no transition, no role check and no
  readiness evaluation. The snapshot still records the status; nothing reads it back. The cost is that a diff of
  a restore against the version it restored shows that one field, which is the honest reading: the content is
  identical and the editorial state genuinely is not.
- **Nothing else.** `IRecipeDataLayer.TryTransitionAsync` is the only write path to the column.

## Editing an approved recipe

An edit succeeds and reopens the recipe to `InDevelopment`, in the edit's own transaction, with
`RecipeStatusTransitions.EditReopenReason` as its reason. An approval is a claim that somebody cleared *these
words*, so leaving it standing over content nobody approved would make the claim untrue.

Two deliberate asymmetries:

- **No role check.** The reopen rule asks for an Editor; this reopen is a consequence of an edit rather than a
  move anybody requested. A Contributor allowed to edit the recipe is allowed to edit it, and refusing them over
  a transition they did not ask for would make the edit route's permissions depend on a state whose significance
  they cannot see. The transition row still names them as the actor.
- **No audit entry.** The edit seam writes none — a recipe's version history records an edit with more fidelity
  than an audit summary could, which is also why the seam has no actor account id to hand. The transition row
  records the reopen with its actor, instant, both states and reason, which is strictly more than the log would
  say. Every transition somebody *asked* for is still audited.

Only `Approved` reopens. `Testing` and `ReadyForReview` assert that work is underway rather than that content
was cleared, and editing a recipe while testing it is how a test kitchen runs; approval re-evaluates readiness
from scratch anyway, so nothing an edit does can slip past the gate through those states.

## A known wrinkle: the approval version reads as untested

`RecipeVersion` is `IImmutableRecord`, so an approval cannot mark the tested version `Ready` — it has to write
a new one. That new version is a byte-identical copy of its parent, and TESTRUN-004's
`recipe.testing.currentVersionUntested` asks whether any test run names the *latest* version. So immediately
after an approval, a readiness evaluation of that recipe reports a blocker against the snapshot the approval
just wrote.

It is consistent and it errs in the safe direction — nothing is ever approved on evidence that does not exist,
and the rule asks for a test of the words as they now stand. It is also mildly redundant: a creator who
approves, reopens, advances and approves again is asked to record a test of content identical to one they
already tested.

Left as it stands rather than patched, because every fix is a decision of its own: teaching the rule to walk
back over consecutive `ReadinessApproval` ancestors needs either an unbounded version walk per evaluation or a
bounded probe with a documented fallback, and recording the inheritance on the version instead needs a column
and a migration. Both change the meaning of a readiness rule that was approved separately. Worth revisiting
when the readiness surface exists and somebody can see how often it actually reads oddly.

## Audit codes

`recipe.archived` and `recipe.unarchived` keep their meaning. `recipe.approved` is new and separate, because
auth.md names approval among the operations that must be separately auditable — "who approved this, and when"
is a question somebody asks on its own. Every other move shares `recipe.status_transitioned`; the transition
row carries which move it was, and a code per pair would be a dozen append-only constants whose only reader is
a filter that could as easily read the row.

## Persistence

`RecipeStatusTransitions` is workspace-owned and `IImmutableRecord`. Foreign keys follow `RecipeVersion`:
`Workspaces` cascades, `Recipes` restricts, and both version references restrict — one cascade path into the
table, which is the shape SQL Server accepts. `CK_RecipeStatusTransitions_Approval_Columns` binds the three
approval columns to `ToStatus = Approved` so provenance cannot be half-recorded, and
`CK_RecipeStatusTransitions_States_Differ` keeps a repeat out of the table.

The status change, the transition row, the audit entry and — on an approval — the version and its snapshot all
commit in one save. `RecipeVersion.Id` is assigned in code, so the transition can name the version it wrote
without a second save.

## Left for the UI prompt

The recipe editor's status control is **gone**. It could only ever have carried `Draft`, and with `Draft` its
only option a recipe in `Testing` or `Approved` would have rendered as a select reading "Draft" — a wrong
answer where there used to be a right one. An edit no longer sends a status at all.

What a creator has lost is the ability to mark a recipe finished from that form, which is exactly the bypass
this machine closes: that move needs an Editor and a clear readiness evaluation. **Until the transition UI
exists, there is no way to advance a recipe from the browser** — archiving and bringing back still work, and a
recipe otherwise stays a draft. The recipe library filters on all six states.
