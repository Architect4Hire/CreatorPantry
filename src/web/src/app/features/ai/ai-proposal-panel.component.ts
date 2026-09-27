import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  model,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpDiffKind,
  CpDiffLegendComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  cpDiffGlyph,
  cpDiffLabel,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import {
  AI_TASK_DISCRIMINATORS,
  AiChangeKind,
  AiChangeTargetKind,
  AiDispositionDecision,
  AiOperationScope,
  AiProposalDispositionResult,
  AiProposalStatus,
  AiProposalWarning,
  AiProposedChange,
  AiTaskType,
  AiWarningKind,
  isTerminalAiStatus,
} from '../../models/ai-proposal.models';
import { AiProposalService, AiProposalStatusOutcome } from '../../services/ai-proposal.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiOperationStatusComponent, AiStatusConnection, isRetryableAiOutcome } from './ai-operation-status.component';

/** Deciding a proposal writes a recipe version, so it carries the same bar as editing one. */
const DISPOSITION_ROLES: readonly WorkspaceRole[] = ['Contributor', 'Editor', 'Owner'];

/** How often a request in flight is asked about. */
const POLL_INTERVAL_MS = 2000;

/** How often it is asked about after a poll failed. Backing off, rather than hammering a server that is down. */
const DEGRADED_POLL_INTERVAL_MS = 8000;

/** After this many failures in a row the panel stops on its own and offers the creator the retry. */
const MAX_CONSECUTIVE_POLL_FAILURES = 3;

/**
 * How long the panel will keep asking before it stops and waits to be asked to look again.
 *
 * A tab left open overnight must not poll until morning. Stopping is visible — the panel says the state may be
 * out of date and offers "Check again" — so nothing is lost by it.
 */
const POLL_CEILING_MS = 5 * 60 * 1000;

/** One proposed change as a reader meets it. */
export interface AiProposalChangeRow {
  readonly change: AiProposedChange;
  readonly key: string;
  readonly kind: CpDiffKind;
  /** The legend's own wording for {@link kind}, rendered as text beside the glyph rather than implied by colour. */
  readonly kindLabel: string;
  readonly label: string;
  readonly before: string | null;
  readonly after: string | null;
  /** Where a move or an add puts the child, counted from one as a reader counts. */
  readonly positionNote: string | null;

  /** Something true of this field that the change row itself cannot say. See `FIELD_NOTES`. */
  readonly fieldNote: string | null;
  /** The verb for {@link positionNote}: an insertion and a reordering are not the same act. */
  readonly positionTag: string | null;
  readonly warnings: readonly AiProposalWarning[];
  readonly hasSafetyCaution: boolean;
  readonly selected: boolean;
  /** The creator's own replacement for {@link after}, once they have written one. */
  readonly editedValue: string | null;
  /** False for a change the panel cannot describe, which it therefore will not offer to accept. */
  readonly acceptable: boolean;
  /** The whole row in one sentence, so a checkbox is not announced as the word "Accept". */
  readonly checkboxLabel: string;
}

/**
 * A value the creator rewrote before deciding, or the withdrawal of one.
 *
 * **The AI's suggestion is not accepted when this is emitted.** The disposition contract carries ids and
 * nothing else — there is nowhere to put an edited value, by design — so an edited change is left out of
 * `acceptedChangeIds` and recorded as declined, and these are the creator's own words for the host to apply
 * through the ordinary recipe edit path. A creator's edit is a creator's edit; recording it as an accepted
 * generation would misreport where the text came from.
 *
 * A `null` value withdraws a wording emitted earlier, and a host that applied it must drop it. Without that
 * event a creator who rewrote a field and then ticked the suggestion instead would have both written: the
 * host's copy of their wording, and the suggestion they accepted, to the same field.
 */
export interface AiProposalChangeEdit {
  readonly change: AiProposedChange;
  readonly value: string | null;
}

/** Why the panel is asking its host to start a fresh request, and what the old one was. */
export interface AiProposalRestartRequest {
  readonly taskType: AiTaskType;
  /** The discriminator a new request would name, or null when the old operation never said what it was. */
  readonly task: string | null;
  readonly scope: AiOperationScope;
  /** The version the stale proposal was computed against — not a version a new request may name. */
  readonly staleSourceVersionId: string | null;
}

/**
 * What the panel is showing. Derived from the server's status plus the four things only the client knows: that
 * a decision is in flight, that one was refused as stale, that the creator stopped watching, and that the last
 * poll did not arrive.
 */
export type AiProposalPanelState =
  | 'idle'
  | 'loading'
  | 'watching'
  | 'proposed'
  | 'deciding'
  | 'decided'
  | 'failed'
  | 'expired'
  | 'stale'
  | 'forbidden'
  | 'gone';

type PanelRefusalKind = 'selection' | 'archived' | 'decided' | 'unavailable' | 'request' | 'forbidden';

interface PanelRefusal {
  readonly kind: PanelRefusalKind;
  readonly message: string;
}

const CHANGE_KINDS: Readonly<Record<AiChangeKind, CpDiffKind>> = {
  // Never stored — a check constraint refuses it — so this is defensive. `warning` rather than `changed`
  // because the panel genuinely cannot say what such a row would do, and presenting it as a rewording would
  // be inventing the one thing a diff must not invent.
  Unspecified: 'warning',
  Set: 'changed',
  Add: 'added',
  Remove: 'removed',
  Move: 'moved',
};

/** What each target is called when a change names no field — an add, a removal or a move. */
const TARGET_LABELS: Readonly<Record<AiChangeTargetKind, string>> = {
  Unspecified: 'Suggestion',
  Recipe: 'Recipe',
  Ingredient: 'Ingredient',
  IngredientGroup: 'Ingredient group',
  InstructionStep: 'Step',
  InstructionGroup: 'Instruction group',
  Equipment: 'Equipment',
  AssetLink: 'Media',
  Tag: 'Tag',
};

/**
 * What each settable field is called, keyed as `targetKind.fieldName` where the bare name is ambiguous.
 *
 * `title` is the ambiguous one: a recipe has a title and so does an instruction group. Everything absent from
 * here is humanised from the server's own name, so a field added to `AiDiffFields` reads sensibly the day it
 * ships rather than waiting for this map to be updated.
 */
const FIELD_LABELS: Readonly<Record<string, string>> = {
  'Recipe.title': 'Title',
  'InstructionGroup.title': 'Group heading',
  'InstructionStep.text': 'Step',
  'InstructionStep.note': 'Step note',
  'InstructionStep.durationMinutes': 'Step duration (minutes)',
  'InstructionStep.temperatureValue': 'Step temperature',
  attributionText: 'Attribution',
  headnote: 'Headnote',
  storageNotes: 'Storage notes',
  prepTimeMinutes: 'Prep time (minutes)',
  cookTimeMinutes: 'Cook time (minutes)',
  restTimeMinutes: 'Rest time (minutes)',
  totalTimeMinutes: 'Total time (minutes)',
  yieldText: 'Yield',
  yieldQuantity: 'Yield quantity',
  servingCount: 'Servings',
  servingSize: 'Serving size',
};

/**
 * Something true of a field that its own change row cannot state.
 *
 * `temperatureValue` is the one that matters. `AiDiffFields` makes it settable and deliberately excludes
 * `temperatureUnitId` — a model may not name an identifier — so the row carries a bare number, and "was 175,
 * suggested 190" says nothing about which scale either number is in. `AiChangeApplicability` refuses the change
 * on a step with no temperature unit, so the step always has one already; this says so, rather than leaving a
 * creator to accept a temperature whose scale is not on screen.
 */
const FIELD_NOTES: Readonly<Record<string, string>> = {
  'InstructionStep.temperatureValue':
    'In the scale already set on this step — a suggestion cannot change that. Check it before accepting.',
};

/** What each flag is called. Assumptions and cautions read as one vocabulary, as `AiWarningKind` intends. */
const WARNING_LABELS: Readonly<Record<AiWarningKind, string>> = {
  Unspecified: 'Note',
  Assumption: 'Assumed',
  CulinaryCaution: 'Worth your judgement',
  NonScalableLanguage: "Doesn't scale or convert",
  UnverifiedClaim: 'Not verified',
  SafetyCaution: 'Check this yourself',
};

/** "storageNotes" -> "Storage notes", "yieldQuantity" -> "Yield quantity". */
function humanise(name: string): string {
  const spaced = name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** Makes each panel's ids unique, so two panels on one page cannot name each other's controls. */
let nextInstance = 0;

/**
 * Review one AI proposal and decide what to do with it.
 *
 * **Nothing here is saved until a disposition commits.** The panel renders suggestions, never the recipe: it
 * writes nothing optimistically, it reports only what the server's reply said, and every state says in words
 * that the recipe has not changed. A creator who closes this page loses a suggestion, never their work.
 *
 * **The diff is the server's.** `beforeValue` was computed from the pinned source version; this component
 * compares nothing, matches nothing and re-orders nothing. A second opinion computed here could disagree with
 * the server about what a creator is approving, which is the reason the route returns the answer rather than
 * the inputs.
 *
 * **The selection rules are the server's too.** Accepting names ids the server issued, an accept-all must name
 * every one of them, and a refused selection writes nothing — the panel surfaces the refusal rather than
 * working around it. It cannot construct a change, edit a stored one, or accept onto a recipe that has moved on.
 *
 * The host owns which task to ask for and when: this panel takes the id of a request that already exists. It
 * publishes the id again through {@link requestId} when asking again creates a new one, so a host that keeps
 * that id in the URL survives a refresh — and the panel itself resumes from whatever state the server has when
 * it is constructed with one, which is what a refresh reduces to.
 */
@Component({
  selector: 'cp-ai-proposal-panel',
  standalone: true,
  imports: [
    FormsModule,
    AiOperationStatusComponent,
    CpButtonComponent,
    CpCheckboxComponent,
    CpDiffLegendComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
  ],
  templateUrl: './ai-proposal-panel.component.html',
  styleUrl: './ai-proposal-panel.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiProposalPanelComponent {
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The request to watch, or null for nothing to show.
   *
   * A two-way model rather than a plain input because asking again creates a *new* operation — a retried
   * request is a new row server-side, so that the provenance recorded against an accepted proposal can never
   * be ambiguous — and the host needs the new id to keep watching it after a refresh.
   */
  readonly requestId = model<string | null>(null);

  /** A decision committed. Carries the server's reply, including what it did and did not write. */
  readonly decided = output<AiProposalDispositionResult>();

  /** The creator rewrote a suggested value. See {@link AiProposalChangeEdit} — this is their edit, not an accept. */
  readonly changeEdited = output<AiProposalChangeEdit>();

  /** The proposal cannot be accepted onto the recipe as it now stands. Only the host knows the new source version. */
  readonly restartRequested = output<AiProposalRestartRequest>();

  private readonly proposals = inject(AiProposalService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirmService = inject(ConfirmService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  private readonly instance = nextInstance++;
  readonly headingId = `cp-ai-proposal-heading-${this.instance}`;
  readonly commentFieldId = `cp-ai-proposal-comment-${this.instance}`;
  readonly outcomeId = `cp-ai-proposal-outcome-${this.instance}`;

  /** The last thing the server said about this request. Null until the first poll answers. */
  private readonly operationSignal = signal<AiProposalStatus | null>(null);
  readonly operation = this.operationSignal.asReadonly();

  /** A read that could not be retried into success: the request is gone, or unreadable. */
  private readonly watchProblem = signal<'not_found' | 'forbidden' | null>(null);

  private readonly consecutiveFailures = signal(0);
  private readonly pollStopped = signal(false);

  /** False once the creator stops checking. Client-side only — see {@link stopChecking}. */
  private readonly watching = signal(true);

  /** Bumped to start a fresh poll loop over the same request. */
  private readonly resumeToken = signal(0);

  private readonly deciding = signal(false);
  private readonly asking = signal(false);

  /**
   * The server refused this member's decision, although their membership says otherwise.
   *
   * Kept apart from {@link watchProblem}, which stands for "this cannot be read at all". A role downgraded
   * mid-review must not wipe a proposal off the screen: the creator may still read it, and the refusal belongs
   * beside the actions it refused rather than in place of the content.
   */
  private readonly decideRefused = signal(false);
  private readonly sourceStale = signal(false);
  private readonly refusalSignal = signal<PanelRefusal | null>(null);
  readonly refusal = this.refusalSignal.asReadonly();

  private readonly decisionSignal = signal<AiProposalDispositionResult | null>(null);

  private readonly selectedIds = signal<ReadonlySet<string>>(new Set());
  private readonly edits = signal<ReadonlyMap<string, string>>(new Map());

  readonly editingChangeId = signal<string | null>(null);
  readonly editDraft = signal('');

  readonly wasHelpful = signal<boolean | null>(null);
  readonly comment = signal('');

  /** Reused when the same "ask again" is re-sent after a transport failure; replaced for a fresh ask. */
  private pendingAskKey: string | null = null;

  /** Set when "Use my wording" was pressed on an empty box, so the field itself can say why nothing happened. */
  readonly editError = signal('');

  constructor() {
    // One loop per (request, resume) pair. The key deliberately does not include the status, or every
    // transition would tear the loop down and immediately re-ask.
    toObservable(this.pollKey)
      .pipe(
        switchMap((key) => {
          if (key === null) return EMPTY;
          this.pollStopped.set(false);
          return this.pollLoop(key.requestId);
        }),
        takeUntilDestroyed(),
      )
      .subscribe({ next: (outcome) => this.applyWatchOutcome(outcome) });

    // Asked for rather than assumed. Until the memberships are known this panel cannot say what the creator may
    // do, and `rolesKnown` keeps it from claiming a role fact in the meantime.
    void this.memberships.ensureLoaded();
  }

  private readonly pollKey = computed<{ readonly requestId: string; readonly resume: number } | null>(() => {
    const requestId = this.requestId();
    if (requestId === null || !this.watching()) return null;
    return { requestId, resume: this.resumeToken() };
  });

  /**
   * Asks, then keeps asking until there is nothing left to learn.
   *
   * `expand` rather than a fixed interval because the delay is not fixed: a failed poll backs off, and the
   * decision to stop depends on what the last answer was. The counters live in this closure rather than in
   * signals so that the loop's own behaviour cannot depend on the order in which a subscriber updated state.
   *
   * It stops on a terminal status, on `Proposed` (nothing moves server-side while a creator is deciding), on a
   * request that is gone or unreadable, after {@link MAX_CONSECUTIVE_POLL_FAILURES}, and at
   * {@link POLL_CEILING_MS}. Every one of those stops is visible in the panel.
   */
  private pollLoop(requestId: string): Observable<AiProposalStatusOutcome> {
    const deadline = Date.now() + POLL_CEILING_MS;
    let failures = 0;

    const ask = (): Observable<AiProposalStatusOutcome> =>
      this.proposals.watchStatus(this.workspaceSlug(), this.recipeId(), requestId);

    const again = (outcome: AiProposalStatusOutcome): Observable<AiProposalStatusOutcome> => {
      if (outcome.status === 'not_found' || outcome.status === 'forbidden') return EMPTY;
      if (outcome.status === 'found' && !AiProposalPanelComponent.needsWatching(outcome.operation)) return EMPTY;

      failures = outcome.status === 'unavailable' ? failures + 1 : 0;
      if (failures >= MAX_CONSECUTIVE_POLL_FAILURES) return EMPTY;
      if (Date.now() >= deadline) return EMPTY;

      return timer(failures === 0 ? POLL_INTERVAL_MS : DEGRADED_POLL_INTERVAL_MS).pipe(switchMap(ask));
    };

    return defer(ask).pipe(
      expand(again),
      // `finalize` would also fire on teardown; this fires only when the loop itself decided to stop, which is
      // the case the panel has something to say about.
      (source) =>
        new Observable<AiProposalStatusOutcome>((subscriber) =>
          source.subscribe({
            next: (value) => subscriber.next(value),
            error: (error: unknown) => subscriber.error(error),
            complete: () => {
              this.pollStopped.set(true);
              subscriber.complete();
            },
          }),
        ),
    );
  }

  /** Whether anything can still change without the creator doing something. */
  private static needsWatching(operation: AiProposalStatus): boolean {
    return !isTerminalAiStatus(operation.status) && operation.status !== 'Proposed';
  }

  private applyWatchOutcome(outcome: AiProposalStatusOutcome): void {
    if (outcome.status === 'found') {
      this.consecutiveFailures.set(0);
      this.watchProblem.set(null);
      this.operationSignal.set(outcome.operation);
      this.reconcileSelection(outcome.operation);
      return;
    }

    if (outcome.status === 'unavailable') {
      // The last known state stays on screen. Blanking a proposal a creator is part-way through reviewing
      // because one poll failed would lose their selection for no reason.
      this.consecutiveFailures.update((count) => count + 1);
      return;
    }

    this.watchProblem.set(outcome.status);
  }

  /**
   * Drops selections and edits for changes the latest read no longer contains.
   *
   * A selection is only ever a set of ids the server published; keeping one it has stopped publishing would
   * mean sending an id the disposition must refuse.
   */
  private reconcileSelection(operation: AiProposalStatus): void {
    const available = new Set((operation.proposal?.changes ?? []).map((change) => change.changeId));

    this.selectedIds.update((selected) => {
      const next = new Set([...selected].filter((id) => available.has(id)));
      return next.size === selected.size ? selected : next;
    });

    this.edits.update((edits) => {
      const next = new Map([...edits].filter(([id]) => available.has(id)));
      return next.size === edits.size ? edits : next;
    });
  }

  // -------------------------------------------------------------------------
  // Derived view
  // -------------------------------------------------------------------------

  readonly proposal = computed(() => this.operation()?.proposal ?? null);

  readonly connection = computed<AiStatusConnection>(() =>
    this.consecutiveFailures() > 0 ? 'degraded' : 'live',
  );

  readonly state = computed<AiProposalPanelState>(() => {
    if (this.requestId() === null) return 'idle';

    const problem = this.watchProblem();
    if (problem === 'not_found') return 'gone';
    if (problem === 'forbidden') return 'forbidden';

    const operation = this.operation();
    if (operation === null) return 'loading';

    // Client-side facts first: a stale refusal means the diff on screen describes content that is no longer
    // there, whatever the operation's own status still says.
    if (this.sourceStale()) return 'stale';
    if (this.deciding() || this.asking()) return 'deciding';

    switch (operation.status) {
      case 'Requested':
      case 'Running':
        return 'watching';
      case 'Proposed':
        return operation.proposal === null ? 'watching' : 'proposed';
      case 'Accepted':
      case 'PartiallyAccepted':
      case 'Rejected':
        return 'decided';
      case 'Failed':
        return 'failed';
      case 'Expired':
        return 'expired';
    }
  });

  /**
   * Whether the creator's role here is known yet.
   *
   * "We don't know" and "you may not" are different things, and only one of them is safe to say out loud. A
   * panel mounted before the shell has settled, or after that read failed, hides the controls — it does not
   * tell an owner they are a reader.
   */
  readonly rolesKnown = computed(() => this.memberships.state().status === 'ready');

  /** Whether this member's role permits deciding at all. The server is still the authority. */
  readonly canDecide = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;
    return role !== undefined && DISPOSITION_ROLES.includes(role);
  });

  /**
   * Whether to offer the decision controls at all — the role, and the server not having already refused.
   *
   * The server is the authority on both. This exists so that a refusal is not offered again a second later to
   * be refused a second time.
   */
  readonly mayDecide = computed(() => this.canDecide() && !this.decideRefused());

  readonly rows = computed<readonly AiProposalChangeRow[]>(() => {
    const proposal = this.proposal();
    if (proposal === null) return [];

    const selected = this.selectedIds();
    const edits = this.edits();

    return proposal.changes.map((change) => {
      const warnings = proposal.warnings.filter((warning) => warning.changeId === change.changeId);
      const hasSafetyCaution = warnings.some((warning) => warning.kind === 'SafetyCaution');
      const kind = CHANGE_KINDS[change.changeKind];
      const label = AiProposalPanelComponent.labelFor(change);
      const acceptable = change.changeKind !== 'Unspecified';

      return {
        change,
        key: change.changeId,
        kind,
        kindLabel: cpDiffLabel(kind),
        label,
        before: change.beforeValue,
        after: change.afterValue,
        positionNote: AiProposalPanelComponent.positionNote(change),
        positionTag: change.changeKind === 'Add' ? 'adds at' : change.changeKind === 'Move' ? 'moves to' : null,
        fieldNote: change.fieldName === null ? null : (FIELD_NOTES[`${change.targetKind}.${change.fieldName}`] ?? null),
        warnings,
        hasSafetyCaution,
        selected: selected.has(change.changeId),
        editedValue: edits.get(change.changeId) ?? null,
        acceptable,
        checkboxLabel: AiProposalPanelComponent.checkboxLabel(change, label, hasSafetyCaution),
      };
    });
  });

  /** Warnings about the proposal as a whole. Per-change ones ride on their row instead. */
  readonly generalWarnings = computed(() =>
    (this.proposal()?.warnings ?? []).filter((warning) => warning.changeId === null),
  );

  /**
   * Only the kinds this proposal actually contains, so the legend explains nothing a reader will not meet — and,
   * more importantly, never omits one they will. The defensive `warning` glyph is the reason this is derived
   * rather than fixed: a row can print a glyph no fixed list would have explained.
   */
  readonly legendKinds = computed<CpDiffKind[]>(() => {
    const order: CpDiffKind[] = ['added', 'removed', 'changed', 'moved', 'warning'];
    const present = new Set(this.rows().map((row) => row.kind));
    return order.filter((kind) => present.has(kind));
  });

  /**
   * Whether the creator has typed something this panel has not sent anywhere.
   *
   * Exposed so a host can fold it into its own navigation guard: an open edit box and an unsent comment are
   * work, and nothing below this component can see them.
   */
  readonly hasUnsentText = computed(
    () => (this.editingChangeId() !== null && this.editDraft().trim().length > 0) || this.comment().trim().length > 0,
  );

  readonly acceptableRows = computed(() => this.rows().filter((row) => row.acceptable));
  readonly selectedCount = computed(() => this.rows().filter((row) => row.selected).length);
  readonly editedCount = computed(() => this.edits().size);

  /**
   * Whether accept-all can be offered.
   *
   * It cannot once anything has been edited: accept-all means "take every suggestion exactly as written", and
   * an edited row is precisely the creator saying they will not. Accept selected still works, and the sentence
   * beside the disabled button says so.
   */
  readonly canAcceptAll = computed(
    () => this.acceptableRows().length > 0 && this.acceptableRows().length === this.rows().length && this.editedCount() === 0,
  );

  readonly acceptAllBlockedReason = computed(() => {
    if (this.rows().length === 0) return 'There are no suggestions to accept.';
    if (this.editedCount() > 0) {
      return "You've rewritten a suggestion, so these can't all be accepted as written. Use Accept selected instead.";
    }
    if (this.acceptableRows().length !== this.rows().length) {
      return 'One of these suggestions cannot be described, so the whole set cannot be accepted at once.';
    }
    return null;
  });

  readonly acceptSelectedBlockedReason = computed(() =>
    this.selectedCount() === 0 ? 'Tick the suggestions you want to keep first.' : null,
  );

  /** Whether asking again is worth offering for this outcome. */
  readonly canAskAgain = computed(() => {
    const operation = this.operation();
    if (operation === null) return false;
    if (!isRetryableAiOutcome(operation.status, operation.failureCategory)) return false;

    // A task nobody can name, a scope nobody declared and a source nobody pinned cannot be asked for again
    // from here: the server's own validator refuses all three, so offering the button would offer a refusal.
    return (
      AI_TASK_DISCRIMINATORS[operation.taskType] !== null &&
      operation.scope !== 'Unspecified' &&
      operation.sourceVersionId !== null &&
      this.mayDecide()
    );
  });

  /** Whether the panel has stopped asking on its own and is waiting to be told to look again. */
  readonly canCheckAgain = computed(() => {
    if (this.requestId() === null) return false;
    if (!this.watching()) return true;

    const operation = this.operation();
    return this.pollStopped() && (operation === null || AiProposalPanelComponent.needsWatching(operation));
  });

  readonly isWatching = computed(() => this.watching() && !this.pollStopped());

  readonly decision = this.decisionSignal.asReadonly();

  /**
   * What the decision did, in the creator's terms.
   *
   * Only ever built from the server's own reply. `recipeVersionNumber` is null for three reasons a client
   * cannot tell apart — rejected, nothing to write, or a replay — so a null never becomes an invented version
   * number, and the wording covers all three honestly.
   */
  readonly outcomeSummary = computed(() => {
    const result = this.decision();
    if (result === null) return null;

    const accepted = result.acceptedChangeCount;
    const declined = result.rejectedChangeCount;

    if (result.status === 'Rejected') {
      return `Declined. Nothing was changed, and ${AiProposalPanelComponent.count(declined, 'suggestion')} ${declined === 1 ? 'was' : 'were'} recorded as declined.`;
    }

    const kept = `Kept ${AiProposalPanelComponent.count(accepted, 'suggestion')}${declined > 0 ? `, declined ${declined}` : ''}.`;

    return result.recipeVersionNumber === null
      ? `${kept} Nothing further to write — your recipe already reflects these changes.`
      : `${kept} Your recipe is now at version ${result.recipeVersionNumber}.`;
  });

  // -------------------------------------------------------------------------
  // Selection
  // -------------------------------------------------------------------------

  onRowToggled(row: AiProposalChangeRow, checked: boolean): void {
    if (!row.acceptable) return;

    this.selectedIds.update((selected) => {
      const next = new Set(selected);
      if (checked) {
        next.add(row.change.changeId);
      } else {
        next.delete(row.change.changeId);
      }
      return next;
    });

    // Ticking a row means taking the suggestion as written, which is the opposite of the edit standing beside
    // it. Dropping the edit here is what keeps the two from disagreeing about what is being accepted.
    if (checked && this.edits().has(row.change.changeId)) {
      this.dropEdit(row.change.changeId);
    }
  }

  /**
   * Ticks everything that has not been rewritten.
   *
   * It leaves the rewritten rows alone rather than selecting them and discarding the creator's text: one click
   * must not destroy words somebody typed, and the count beside the button says how many were left out. Keeping
   * both would be the contradiction {@link onRowToggled} avoids per row.
   */
  selectAll(): void {
    const edits = this.edits();
    this.selectedIds.set(
      new Set(
        this.acceptableRows()
          .filter((row) => !edits.has(row.change.changeId))
          .map((row) => row.change.changeId),
      ),
    );
  }

  clearSelection(): void {
    this.selectedIds.set(new Set());
  }

  // -------------------------------------------------------------------------
  // Edit before accept
  // -------------------------------------------------------------------------

  startEdit(row: AiProposalChangeRow): void {
    this.editingChangeId.set(row.change.changeId);
    this.editDraft.set(row.editedValue ?? row.after ?? '');
    this.editError.set('');
    this.focusById(this.editInputId(row.change.changeId));
  }

  commitEdit(row: AiProposalChangeRow): void {
    const value = this.editDraft().trim();
    if (value.length === 0) {
      // Said beside the control rather than left as a button that appears to do nothing.
      this.editError.set('Write the wording you want, or cancel to keep the suggestion as it is.');
      return;
    }

    this.editError.set('');

    this.edits.update((edits) => new Map(edits).set(row.change.changeId, value));

    // Not accepted: the creator's words go to the host as an ordinary edit, and the change itself is declined
    // when the disposition is sent. See AiProposalChangeEdit.
    this.selectedIds.update((selected) => {
      if (!selected.has(row.change.changeId)) return selected;
      const next = new Set(selected);
      next.delete(row.change.changeId);
      return next;
    });

    this.editingChangeId.set(null);
    this.changeEdited.emit({ change: row.change, value });
    this.focusById(this.editButtonId(row.change.changeId));
  }

  cancelEdit(row: AiProposalChangeRow): void {
    this.editingChangeId.set(null);
    this.editError.set('');
    this.focusById(this.editButtonId(row.change.changeId));
  }

  undoEdit(row: AiProposalChangeRow): void {
    this.dropEdit(row.change.changeId);
    this.focusById(this.editButtonId(row.change.changeId));
  }

  /**
   * Forgets one wording, and tells the host so.
   *
   * The single place an edit is dropped, precisely so the withdrawal cannot be forgotten on one of the paths
   * that drops one — ticking the row, or undoing. A host that has already applied the wording needs to hear
   * that it no longer stands.
   */
  private dropEdit(changeId: string): void {
    if (!this.edits().has(changeId)) return;

    this.edits.update((edits) => {
      const next = new Map(edits);
      next.delete(changeId);
      return next;
    });

    const change = this.proposal()?.changes.find((entry) => entry.changeId === changeId);
    if (change !== undefined) this.changeEdited.emit({ change, value: null });
  }

  editInputId(changeId: string): string {
    return `cp-ai-proposal-edit-${this.instance}-${changeId}`;
  }

  editButtonId(changeId: string): string {
    return `cp-ai-proposal-edit-button-${this.instance}-${changeId}`;
  }

  // -------------------------------------------------------------------------
  // Decisions
  // -------------------------------------------------------------------------

  async acceptSelected(): Promise<void> {
    const ids = this.rows()
      .filter((row) => row.selected)
      .map((row) => row.change.changeId);
    if (ids.length === 0) return;

    await this.decide('AcceptSelected', ids);
  }

  /** Names every change, because that is what the server requires an accept-all to confirm. */
  async acceptAll(): Promise<void> {
    if (!this.canAcceptAll()) return;
    await this.decide('AcceptAll', this.rows().map((row) => row.change.changeId));
  }

  /**
   * Declines the whole proposal, after asking.
   *
   * The one confirmation in this panel, and it earns it: a proposal can be decided once, so declining cannot
   * be undone and the suggestions cannot be looked at again. Accepting needs no confirmation of its own —
   * ticking the changes *is* the confirmation the server requires, and an accepted change can be reverted by
   * restoring a version.
   */
  async reject(): Promise<void> {
    const confirmed = await this.confirmService.confirm({
      title: 'Decline these suggestions?',
      message:
        "They can't be reviewed again afterwards. Your recipe stays exactly as it is, and you can always ask for new suggestions.",
      confirmLabel: 'Decline suggestions',
      cancelLabel: 'Keep reviewing',
      tone: 'danger',
    });
    if (!confirmed) return;

    await this.decide('Reject', []);
  }

  private async decide(decision: AiDispositionDecision, acceptedChangeIds: readonly string[]): Promise<void> {
    const requestId = this.requestId();
    if (requestId === null || this.deciding()) return;

    this.deciding.set(true);
    this.refusalSignal.set(null);

    const outcome = await this.proposals.disposition(this.workspaceSlug(), this.recipeId(), requestId, {
      decision,
      acceptedChangeIds,
      wasHelpful: this.wasHelpful(),
      comment: this.comment(),
    });

    this.deciding.set(false);

    switch (outcome.status) {
      case 'decided':
        this.decisionSignal.set(outcome.result);
        // The reply's status is the operation's new status, so the panel does not need another read to stop
        // showing actions — and must not keep showing them while it waits for one.
        this.operationSignal.update((operation) =>
          operation === null ? operation : { ...operation, status: outcome.result.status, statusChangedAt: outcome.result.decidedAt },
        );
        this.selectedIds.set(new Set());
        this.decided.emit(outcome.result);
        this.focusById(this.outcomeId);
        // Then read once more, because the reply says what the operation became and not what became of each
        // change. Without this the rows keep the `Pending` disposition they were read with and report "Not
        // decided" about changes the server has just accepted. Mapping the accepted ids in locally would be the
        // client inventing the server's answer, which is the one thing this panel must never do.
        this.checkAgain();
        return;

      case 'selection_invalid':
        // The server's own sentence: it names which change was the problem, and nothing was written.
        this.refusalSignal.set({ kind: 'selection', message: outcome.message });
        this.checkAgain();
        return;

      case 'source_stale':
        this.sourceStale.set(true);
        this.refusalSignal.set({
          kind: 'selection',
          message:
            'Your recipe changed while these suggestions were waiting, so they describe wording that is no longer there. Nothing was saved.',
        });
        return;

      case 'recipe_archived':
        this.refusalSignal.set({
          kind: 'archived',
          message: 'This recipe is archived, so it will not accept changes. Bring it back from the archive first.',
        });
        return;

      case 'already_decided':
        this.refusalSignal.set({
          kind: 'decided',
          message: 'This proposal has already been decided. Nothing was changed by this attempt.',
        });
        this.checkAgain();
        return;

      case 'forbidden':
        // Not `watchProblem`: they can still read this, and taking the proposal off the screen would throw away
        // a review in progress to report a refusal about it.
        this.decideRefused.set(true);
        this.refusalSignal.set({
          kind: 'forbidden',
          message:
            'Your role in this workspace does not permit deciding this proposal. Nothing was changed. Ask an '
            + 'editor or the owner to decide it.',
        });
        return;

      case 'not_found':
        this.watchProblem.set('not_found');
        return;

      case 'validation_failed':
        this.refusalSignal.set({
          kind: 'selection',
          message:
            Object.values(outcome.fieldErrors)[0]?.[0] ?? 'That decision could not be recorded as submitted.',
        });
        return;

      case 'unavailable':
        // Deliberately not "nothing was saved". The disposition commits the version, the per-change decisions,
        // the feedback and the terminal status in one transaction, and a connection that drops after that
        // commit looks identical from here to one that drops before it. Saying which would be a guess about the
        // creator's recipe, so this says what is true and then finds out.
        this.refusalSignal.set({
          kind: 'unavailable',
          message:
            "Couldn't reach the server, so whether this was recorded is unconfirmed. Checking now — your "
            + 'selection is still here.',
        });
        this.checkAgain();
        return;
    }
  }

  // -------------------------------------------------------------------------
  // Asking again, checking again, stopping
  // -------------------------------------------------------------------------

  /**
   * Asks for a fresh proposal of the same kind, against the same version.
   *
   * A new operation with a new key, never a revival of the old one. If the recipe has moved on in the
   * meantime the server refuses the source version, and that refusal lands in the same stale-recovery state a
   * refused acceptance does — because the remedy is identical.
   */
  async askAgain(): Promise<void> {
    const operation = this.operation();
    if (operation === null || this.asking()) return;

    const task = AI_TASK_DISCRIMINATORS[operation.taskType];
    const sourceVersionId = operation.sourceVersionId;
    if (task === null || sourceVersionId === null) return;

    this.asking.set(true);
    this.refusalSignal.set(null);

    // One key per attempt, reused only if this same attempt has to be re-sent after a transport failure:
    // generating a fresh key for a repeat of the same click would buy a second answer to one question.
    this.pendingAskKey ??= crypto.randomUUID();

    const outcome = await this.proposals.requestProposal(
      this.workspaceSlug(),
      this.recipeId(),
      { task, scope: operation.scope, sourceVersionId },
      this.pendingAskKey,
    );

    this.asking.set(false);

    switch (outcome.status) {
      case 'accepted':
        this.pendingAskKey = null;
        this.resetForNewRequest();
        this.operationSignal.set(outcome.operation);
        this.requestId.set(outcome.operation.aiProposalRequestId);
        return;

      case 'source_version_invalid':
        this.pendingAskKey = null;
        this.sourceStale.set(true);
        this.refusalSignal.set({
          kind: 'request',
          message: 'Your recipe has changed since these suggestions were made, so this has to start again from it.',
        });
        return;

      case 'idempotency_key_conflict':
        // The key has been spent on a different request. A fresh one is the remedy, and the creator asking
        // again is what supplies it.
        this.pendingAskKey = null;
        this.refusalSignal.set({ kind: 'request', message: 'That attempt clashed with another. Try once more.' });
        return;

      case 'task_not_enabled':
        this.pendingAskKey = null;
        this.refusalSignal.set({
          kind: 'request',
          message: 'This kind of help is switched off for now, so it cannot be asked for again.',
        });
        return;

      case 'task_unknown':
        this.pendingAskKey = null;
        this.refusalSignal.set({ kind: 'request', message: 'This kind of help is no longer available.' });
        return;

      case 'forbidden':
        // As in `decide`: a refusal about an action, not about reading, so the outcome on screen stays.
        this.pendingAskKey = null;
        this.decideRefused.set(true);
        this.refusalSignal.set({
          kind: 'forbidden',
          message: 'Your role in this workspace does not permit asking for suggestions on this recipe.',
        });
        return;

      case 'not_found':
        this.pendingAskKey = null;
        this.watchProblem.set('not_found');
        return;

      case 'validation_failed':
      case 'conflict':
        this.pendingAskKey = null;
        this.refusalSignal.set({ kind: 'request', message: 'That request was refused. Nothing was changed.' });
        return;

      case 'unavailable':
        // The key is kept: this attempt may have reached the server even though the answer did not come back,
        // and re-sending the same key is what stops a second answer being bought for it.
        this.refusalSignal.set({ kind: 'unavailable', message: "Couldn't reach the server. Try again in a moment." });
        return;
    }
  }

  /** Hands the restart to the host, which is the only side that knows the recipe's current version. */
  requestRestart(): void {
    const operation = this.operation();
    if (operation === null) return;

    this.restartRequested.emit({
      taskType: operation.taskType,
      task: AI_TASK_DISCRIMINATORS[operation.taskType],
      scope: operation.scope,
      staleSourceVersionId: operation.sourceVersionId,
    });
  }

  /** Starts a fresh poll loop over the same request. */
  checkAgain(): void {
    this.consecutiveFailures.set(0);
    this.watching.set(true);
    this.resumeToken.update((token) => token + 1);
  }

  /**
   * Stops this page from checking. It does **not** cancel the request.
   *
   * There is no route that could: the API offers asking, reading and deciding, and nothing else. A button that
   * claimed to cancel generation would be promising something the server cannot honour, so it says what it
   * actually does and the copy beside it says what continues.
   */
  stopChecking(): void {
    this.watching.set(false);
  }

  private resetForNewRequest(): void {
    this.decisionSignal.set(null);
    this.selectedIds.set(new Set());
    this.edits.set(new Map());
    this.editingChangeId.set(null);
    this.editError.set('');
    this.wasHelpful.set(null);
    this.comment.set('');
    this.sourceStale.set(false);
    this.watchProblem.set(null);
    this.decideRefused.set(false);
    this.consecutiveFailures.set(0);
    this.watching.set(true);
  }

  onHelpfulChange(value: boolean): void {
    // Tapping the same answer again clears it: the question is optional, and nothing should make an accidental
    // click permanent.
    this.wasHelpful.update((current) => (current === value ? null : value));
  }

  glyphFor(kind: CpDiffKind): string {
    return cpDiffGlyph(kind);
  }

  warningLabel(kind: AiWarningKind): string {
    return WARNING_LABELS[kind];
  }

  private focusById(id: string): void {
    afterNextRender(
      () => {
        this.elementRef.nativeElement.querySelector<HTMLElement>(`[id="${id}"]`)?.focus();
      },
      { injector: this.injector },
    );
  }

  private static labelFor(change: AiProposedChange): string {
    if (change.fieldName === null) return TARGET_LABELS[change.targetKind];

    return (
      FIELD_LABELS[`${change.targetKind}.${change.fieldName}`] ??
      FIELD_LABELS[change.fieldName] ??
      humanise(change.fieldName)
    );
  }

  /** Positions are zero-based on the wire and one-based to a reader, who counts from the first line. */
  private static positionNote(change: AiProposedChange): string | null {
    if (change.proposedPosition === null) return null;
    if (change.changeKind !== 'Move' && change.changeKind !== 'Add') return null;
    return `position ${change.proposedPosition + 1}`;
  }

  /**
   * The whole row as one sentence, so the checkbox is announced as the decision it is rather than as the word
   * "Accept" repeated down a list. A safety caution is named here too: accepting one should be deliberate.
   */
  private static checkboxLabel(change: AiProposedChange, label: string, hasSafetyCaution: boolean): string {
    const caution = hasSafetyCaution ? ' Check this one yourself before accepting.' : '';

    switch (change.changeKind) {
      case 'Add':
        return `Accept new ${label}${change.afterValue === null ? '' : `: ${change.afterValue}`}.${caution}`;
      case 'Remove':
        return `Accept removing ${label}${change.beforeValue === null ? '' : `: ${change.beforeValue}`}.${caution}`;
      case 'Move': {
        const to = AiProposalPanelComponent.positionNote(change);
        return `Accept moving ${label} ${to === null ? 'to a new position' : `to ${to}`}.${caution}`;
      }
      case 'Set':
        return (
          `Accept change to ${label}. Was ${AiProposalPanelComponent.quote(change.beforeValue)}, ` +
          `now ${AiProposalPanelComponent.quote(change.afterValue)}.${caution}`
        );
      case 'Unspecified':
        return `${label}: this suggestion cannot be described, so it cannot be accepted here.`;
    }
  }

  private static quote(value: string | null): string {
    return value === null || value.length === 0 ? 'empty' : `“${value}”`;
  }

  private static count(value: number, noun: string): string {
    return `${value} ${noun}${value === 1 ? '' : 's'}`;
  }
}
