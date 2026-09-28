import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, defer, expand, switchMap, timer } from 'rxjs';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  AiProposalStatus,
  AiProposalWarning,
  AiWarningKind,
  isTerminalAiStatus,
} from '../../models/ai-proposal.models';
import {
  RecipeDraft,
  RecipeDraftEquipment,
  RecipeDraftGroup,
  RecipeDraftIngredient,
  RecipeDraftStep,
  countItems,
  draftFromProposal,
  missingRequiredFieldsOf,
} from '../../models/recipe-draft.models';
import { RecipeDraftService, WatchRecipeDraftOutcome } from '../../services/recipe-draft.service';
import { AiOperationStatusComponent, AiStatusConnection, isRetryableAiOutcome } from './ai-operation-status.component';

/** Same cadence as the other two AI surfaces, so all three behave identically. */
const POLL_INTERVAL_MS = 2000;
const DEGRADED_POLL_INTERVAL_MS = 8000;
const MAX_CONSECUTIVE_POLL_FAILURES = 3;
const POLL_CEILING_MS = 5 * 60 * 1000;

export type FirstDraftReviewState =
  | 'idle'
  | 'loading'
  | 'watching'
  | 'proposed'
  | 'failed'
  | 'expired'
  | 'gone'
  | 'forbidden'
  | 'discarded';

/**
 * A value the creator rewrote before anything was accepted, or the withdrawal of one.
 *
 * **Nothing here accepts the model's suggestion, and nothing here creates a recipe.** An edit is the
 * creator's own words, held client-side, for the acceptance step to carry into a new recipe. A `null` value
 * withdraws a wording emitted earlier, so a host that kept it must drop it.
 */
export interface RecipeDraftFieldEdit {
  /** The `Add` row's change id for a line, step or item; a bare field name for a recipe-level field. */
  readonly changeId: string;
  /** Which field of that row, or the recipe-level field name. */
  readonly field: string;
  readonly value: string | null;
}

/** One editable box on screen. The key is what an edit is stored and emitted under. */
interface EditTarget {
  readonly key: string;
  readonly changeId: string;
  readonly field: string;
  readonly label: string;
  readonly original: string;
}

/**
 * Review one AIREC-002 structured first draft: read it, rewrite parts of it, or discard it.
 *
 * **Nothing here creates a recipe, and nothing here is saved.** There is no route this page can call that
 * writes one — see {@link RecipeDraftService}, which has a single read method. Accepting a draft into a new
 * `Recipe` is a separate, explicit step with its own transaction; this page produces a reviewed draft and
 * the creator's own edits, and that is all.
 *
 * **Discarding is client-side, and the page says so rather than implying otherwise.** A workspace-level AI
 * request has no disposition route — the same narrowness the Concept Studio documents — so there is nothing
 * to tell the server. The draft is left to expire on its own, and the wording never claims a rejection was
 * recorded.
 *
 * **This is a different shape of problem from `cp-ai-proposal-panel`, for the reason the Concept Studio gives.**
 * That panel reviews a field-level diff against an existing recipe, per change, against a disposition
 * endpoint. A first draft has no recipe to diff against, no disposition to record, and arrives as a hundred
 * `Add`/`Set` rows whose group membership is positional — rendered as a flat change list it is unreadable. So
 * this page reconstructs the draft with `draftFromProposal` and renders it as the recipe it is proposing to
 * be. It reuses `cp-ai-operation-status` and the same poll/backoff shape, so the three AI surfaces read as
 * one system.
 */
@Component({
  selector: 'cp-recipe-first-draft-review',
  standalone: true,
  imports: [
    FormsModule,
    NgTemplateOutlet,
    AiOperationStatusComponent,
    CpAnchorNavComponent,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
  ],
  templateUrl: './recipe-first-draft-review.component.html',
  styleUrl: './recipe-first-draft-review.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeFirstDraftReviewComponent {
  private readonly drafts = inject(RecipeDraftService);
  private readonly confirmService = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  /** Shared by concurrent leave attempts, so a second one joins the open dialog rather than stacking one. */
  private pendingLeaveConfirm: Promise<boolean> | null = null;

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  /** The creator rewrote a value. Their edit, never an acceptance — see {@link RecipeDraftFieldEdit}. */
  readonly fieldEdited = output<RecipeDraftFieldEdit>();

  /** The creator discarded the draft. Nothing was sent anywhere; the host may clear its own state. */
  readonly discarded = output<void>();

  private readonly requestIdSignal = signal<string | null>(null);
  readonly requestId = this.requestIdSignal.asReadonly();

  private readonly operationSignal = signal<AiProposalStatus | null>(null);
  readonly operation = this.operationSignal.asReadonly();

  private readonly watchProblem = signal<'not_found' | 'forbidden' | null>(null);
  private readonly consecutiveFailures = signal(0);
  private readonly pollStopped = signal(false);
  private readonly watching = signal(true);
  private readonly resumeToken = signal(0);

  /** Client-side only. See the class remarks: there is no route to tell the server about this. */
  private readonly wasDiscarded = signal(false);

  /** The creator's own wording, keyed by {@link EditTarget.key}. Never sent anywhere by this component. */
  private readonly editsSignal = signal<ReadonlyMap<string, string>>(new Map());
  readonly edits = this.editsSignal.asReadonly();

  readonly editingKey = signal<string | null>(null);
  readonly editDraft = signal('');
  readonly editError = signal('');

  /** Half-written text for a row whose editor is not open, keyed like {@link edits}. */
  private readonly inProgressSignal = signal<ReadonlyMap<string, string>>(new Map());

  /**
   * Whether the creator has anything here they would be sorry to lose.
   *
   * All three kinds count: a wording they committed, one they half-wrote and moved away from, and whatever is
   * in the box right now. The navigation guard reads this, and a guard that only noticed committed edits
   * would let the most easily lost of the three go without a word.
   */
  readonly hasUnsavedWork = computed(
    () => this.editsSignal().size > 0 || this.inProgressSignal().size > 0 || this.editDraft().length > 0,
  );

  constructor() {
    this.restoreFromUrl();

    // A creator's rewrites live only in this component — there is nothing to save them to — so leaving the
    // page loses them. `?request=` resumes the draft itself, which makes the loss worse rather than better:
    // they come back to the same draft and find their own words gone. frontend.md asks for unsaved creator
    // edits to survive navigation warnings; the router half is the route's CanDeactivateFn, and this is the
    // half the router never sees.
    const beforeUnloadHandler = (event: BeforeUnloadEvent) => {
      if (!this.hasUnsavedWork()) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnloadHandler);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnloadHandler));

    // One loop per (request, resume) pair — the key deliberately excludes status, or every transition would
    // tear the loop down and re-ask. Mirrors the Concept Studio and the proposal panel.
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
  }

  // -------------------------------------------------------------------------
  // Watching
  // -------------------------------------------------------------------------

  private readonly pollKey = computed<{ readonly requestId: string; readonly resume: number } | null>(() => {
    const requestId = this.requestIdSignal();
    if (requestId === null || !this.watching() || this.wasDiscarded()) return null;
    return { requestId, resume: this.resumeToken() };
  });

  private pollLoop(requestId: string): Observable<WatchRecipeDraftOutcome> {
    const deadline = Date.now() + POLL_CEILING_MS;
    let failures = 0;

    const ask = (): Observable<WatchRecipeDraftOutcome> => this.drafts.watchStatus(this.workspaceSlug, requestId);

    const again = (outcome: WatchRecipeDraftOutcome): Observable<WatchRecipeDraftOutcome> => {
      if (outcome.status === 'not_found' || outcome.status === 'forbidden') return EMPTY;
      if (outcome.status === 'found' && !RecipeFirstDraftReviewComponent.needsWatching(outcome.operation)) {
        return EMPTY;
      }

      failures = outcome.status === 'unavailable' ? failures + 1 : 0;
      if (failures >= MAX_CONSECUTIVE_POLL_FAILURES) return EMPTY;
      if (Date.now() >= deadline) return EMPTY;

      return timer(failures === 0 ? POLL_INTERVAL_MS : DEGRADED_POLL_INTERVAL_MS).pipe(switchMap(ask));
    };

    return defer(ask).pipe(
      expand(again),
      (source) =>
        new Observable<WatchRecipeDraftOutcome>((subscriber) =>
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

  /** Nothing moves server-side once a draft is `Proposed` — there is no disposition route to move it further. */
  private static needsWatching(operation: AiProposalStatus): boolean {
    return !isTerminalAiStatus(operation.status) && operation.status !== 'Proposed';
  }

  private applyWatchOutcome(outcome: WatchRecipeDraftOutcome): void {
    if (outcome.status === 'found') {
      this.consecutiveFailures.set(0);
      this.watchProblem.set(null);
      this.operationSignal.set(outcome.operation);
      return;
    }

    if (outcome.status === 'unavailable') {
      // The last known state stays on screen. One failed poll must not blank a draft a creator is reading.
      this.consecutiveFailures.update((count) => count + 1);
      return;
    }

    this.watchProblem.set(outcome.status);
  }

  // -------------------------------------------------------------------------
  // Derived view
  // -------------------------------------------------------------------------

  readonly connection = computed<AiStatusConnection>(() => (this.consecutiveFailures() > 0 ? 'degraded' : 'live'));

  readonly state = computed<FirstDraftReviewState>(() => {
    if (this.wasDiscarded()) return 'discarded';
    if (this.requestIdSignal() === null) return 'idle';

    const problem = this.watchProblem();
    if (problem === 'not_found') return 'gone';
    if (problem === 'forbidden') return 'forbidden';

    const operation = this.operationSignal();
    if (operation === null) return 'loading';

    switch (operation.status) {
      case 'Requested':
      case 'Running':
        return 'watching';
      case 'Proposed':
        return operation.proposal === null ? 'watching' : 'proposed';
      case 'Failed':
        return 'failed';
      case 'Expired':
        return 'expired';
      // Never reached: nothing decides a first-draft request today.
      case 'Accepted':
      case 'PartiallyAccepted':
      case 'Rejected':
        return 'proposed';
    }
  });

  readonly draft = computed<RecipeDraft>(() => draftFromProposal(this.operation()?.proposal ?? null));

  /**
   * What a recipe cannot be created without and this draft has not supplied.
   *
   * Shown while reviewing rather than discovered at acceptance. AIREC-002's RESTRICTION is that an unresolved
   * value stays explicit, and a creator meeting a missing title as a failure at the end has been told too late.
   */
  readonly missingRequired = computed(() => missingRequiredFieldsOf(this.draft()));

  readonly unresolvedQuestions = computed<readonly AiProposalWarning[]>(() => this.draft().unresolvedQuestions);
  readonly warnings = computed<readonly AiProposalWarning[]>(() => this.draft().warnings);

  readonly hasSafetyCaution = computed(() =>
    this.warnings().some((warning) => warning.kind === 'SafetyCaution'),
  );

  readonly ingredientCount = computed(() => countItems(this.draft().ingredientGroups));
  readonly stepCount = computed(() => countItems(this.draft().instructionGroups));

  /** In-page destinations over one long document. Not tabs — everything is present, this only moves you. */
  readonly sections = computed<readonly CpAnchorNavItem[]>(() => {
    const draft = this.draft();
    const items: CpAnchorNavItem[] = [];

    const unresolved = this.unresolvedQuestions().length;
    if (unresolved > 0) {
      items.push({
        targetId: 'cp-draft-unresolved',
        label: 'Unresolved questions',
        detail: `${unresolved} to answer`,
      });
    }
    if (this.warnings().length > 0) {
      items.push({ targetId: 'cp-draft-warnings', label: 'Worth checking', detail: `${this.warnings().length}` });
    }

    items.push({ targetId: 'cp-draft-overview', label: 'Overview' });
    items.push({ targetId: 'cp-draft-yield', label: 'Yield and timing' });
    items.push({
      targetId: 'cp-draft-ingredients',
      label: 'Ingredients',
      detail: `${this.ingredientCount()} ${this.ingredientCount() === 1 ? 'line' : 'lines'}`,
    });
    items.push({
      targetId: 'cp-draft-method',
      label: 'Method',
      detail: `${this.stepCount()} ${this.stepCount() === 1 ? 'step' : 'steps'}`,
    });
    if (draft.equipment.length > 0) {
      items.push({ targetId: 'cp-draft-equipment', label: 'Equipment', detail: `${draft.equipment.length}` });
    }
    if (draft.unrecognisedFields.length > 0) {
      items.push({
        targetId: 'cp-draft-extra',
        label: 'Also suggested',
        detail: `${draft.unrecognisedFields.length}`,
      });
    }
    items.push({ targetId: 'cp-draft-provenance', label: 'How this was made' });

    return items;
  });

  readonly activeSection = signal<string | null>(null);

  readonly canAskAgain = computed(() => {
    const operation = this.operationSignal();
    return operation !== null && isRetryableAiOutcome(operation.status, operation.failureCategory);
  });

  /** Whether the page has stopped asking on its own and is waiting to be told to look again. */
  readonly canCheckAgain = computed(() => {
    if (this.requestIdSignal() === null || this.wasDiscarded()) return false;
    if (!this.watching()) return true;

    const operation = this.operationSignal();
    return this.pollStopped() && (operation === null || RecipeFirstDraftReviewComponent.needsWatching(operation));
  });

  readonly isWatching = computed(() => this.watching() && !this.pollStopped() && !this.wasDiscarded());

  // -------------------------------------------------------------------------
  // Reading a value
  // -------------------------------------------------------------------------

  /**
   * What to show for a field: the creator's own wording if they wrote one, otherwise the model's.
   *
   * An absent value is never blanked into an empty cell — the template renders {@link NOT_SUPPLIED} for it,
   * so "the model did not propose a rest time" and "the rest time is zero" cannot look the same.
   */
  valueOf(key: string, proposed: string | null): string | null {
    return this.editsSignal().get(key) ?? proposed;
  }

  isEdited(key: string): boolean {
    return this.editsSignal().has(key);
  }

  // -------------------------------------------------------------------------
  // Editing
  // -------------------------------------------------------------------------

  /**
   * Opens the editor for one field.
   *
   * Half-written text is kept when the creator moves to another row and restored when they come back. Only
   * one editor is open at a time, so without this, clicking "Edit" on a second line would silently throw away
   * whatever was typed into the first — the same loss the navigation guard exists to prevent, one screen
   * further in.
   */
  startEdit(target: EditTarget): void {
    this.stashCurrentDraft();

    this.editError.set('');
    this.editDraft.set(this.inProgressSignal().get(target.key) ?? this.valueOf(target.key, target.original) ?? '');
    this.editingKey.set(target.key);
    this.focusEditor();
  }

  /** Closes the editor and keeps the half-written text, so reopening the row resumes it. */
  cancelEdit(): void {
    this.stashCurrentDraft();
    this.closeEditor();
  }

  /** Remembers what is in the open box, unless it is only the value that was already there. */
  private stashCurrentDraft(): void {
    const key = this.editingKey();
    if (key === null) return;

    const typed = this.editDraft();
    if (typed.length > 0) this.inProgressSignal.update((drafts) => new Map(drafts).set(key, typed));
  }

  private forgetInProgress(key: string): void {
    this.inProgressSignal.update((drafts) => {
      const next = new Map(drafts);
      next.delete(key);
      return next;
    });
  }

  private closeEditor(): void {
    const key = this.editingKey();
    this.editingKey.set(null);
    this.editDraft.set('');
    this.editError.set('');
    if (key !== null) this.restoreFocusTo(key);
  }

  /**
   * Records the creator's wording for one field.
   *
   * Emitted, not applied to anything: this page writes nothing, and the edit travels to whatever eventually
   * creates a recipe. A box emptied to whitespace is a refusal rather than a deletion — there is no way to
   * propose "this field should be blank" that is distinguishable from a slip, so it says so and keeps both
   * the model's value and any earlier edit.
   */
  applyEdit(target: EditTarget): void {
    const value = this.editDraft().trim();

    if (value.length === 0) {
      this.editError.set('Write your version, or cancel to keep what was suggested.');
      return;
    }

    this.editsSignal.update((edits) => new Map(edits).set(target.key, value));
    this.forgetInProgress(target.key);
    this.fieldEdited.emit({ changeId: target.changeId, field: target.field, value });
    this.closeEditor();
  }

  /** Withdraws an edit, putting the model's own wording back. Emits `null` so a host drops its copy. */
  revertEdit(target: EditTarget): void {
    this.editsSignal.update((edits) => {
      const next = new Map(edits);
      next.delete(target.key);
      return next;
    });
    this.forgetInProgress(target.key);
    this.fieldEdited.emit({ changeId: target.changeId, field: target.field, value: null });
    this.closeEditor();
  }

  /**
   * Puts focus in the box that just replaced the button the creator pressed.
   *
   * Opening the editor destroys that button, so without this the keyboard user is dropped back to
   * `&lt;body&gt;` — on a page of a hundred rows, that means returning to the top of the document.
   */
  private focusEditor(): void {
    afterNextRender(
      () => this.elementRef.nativeElement.querySelector<HTMLTextAreaElement>('.draft-editor textarea')?.focus(),
      { injector: this.injector },
    );
  }

  /** And the reverse on close: back to the row's own Edit button, not to the top of the page. */
  private restoreFocusTo(key: string): void {
    afterNextRender(
      () =>
        this.elementRef.nativeElement
          .querySelector<HTMLElement>(`[data-edit-for="${CSS.escape(key)}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  /** Builds the identity an edit is stored under. Recipe-level fields have no row, so the field name is it. */
  editTarget(changeId: string, field: string, label: string, original: string | null): EditTarget {
    return { key: `${changeId}:${field}`, changeId, field, label, original: original ?? '' };
  }

  // -------------------------------------------------------------------------
  // Discarding
  // -------------------------------------------------------------------------

  /**
   * Clears the draft from this page.
   *
   * Client-side, and the confirmation says so: there is no route to record a rejection for a workspace-level
   * request, so the draft is simply left to expire. Claiming otherwise would be the one thing a review
   * surface must not do, which is report work it did not do.
   */
  async discard(): Promise<void> {
    const confirmed = await this.confirmService.confirm({
      title: 'Discard this draft?',
      message:
        'It will be cleared from this page and left to expire on its own. Nothing is sent anywhere, and no '
        + 'recipe was created. Your own edits to it are lost.',
      confirmLabel: 'Discard draft',
      cancelLabel: 'Keep reviewing',
      tone: 'danger',
    });

    if (!confirmed) return;

    this.wasDiscarded.set(true);
    this.editingKey.set(null);
    this.editDraft.set('');
    this.editError.set('');
    this.editsSignal.set(new Map());
    this.inProgressSignal.set(new Map());
    this.discarded.emit();
  }

  /**
   * Used by the route's <c>CanDeactivateFn</c>: resolves immediately when there is nothing to lose, and
   * otherwise asks the creator.
   *
   * A browser close or refresh is not covered here and cannot be — see the `beforeunload` listener in the
   * constructor. Discarding on purpose has already cleared the edits by the time this is asked, so leaving
   * afterwards is silent, which is right: they were asked once already.
   */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.hasUnsavedWork()) return Promise.resolve(true);

    this.pendingLeaveConfirm ??= this.confirmService
      .confirm({
        title: 'Leave your edits behind?',
        message:
          "Your rewrites to this draft haven't been kept anywhere — there is nowhere to save them to yet. "
          + 'If you leave now, they are lost. The draft itself will still be here.',
        confirmLabel: 'Leave',
        cancelLabel: 'Keep reviewing',
        tone: 'danger',
      })
      .finally(() => {
        this.pendingLeaveConfirm = null;
      });

    return this.pendingLeaveConfirm;
  }

  // -------------------------------------------------------------------------
  // Checking again, stopping
  // -------------------------------------------------------------------------

  checkAgain(): void {
    this.consecutiveFailures.set(0);
    this.watching.set(true);
    this.resumeToken.update((token) => token + 1);
  }

  /**
   * Stops this page from checking. It does **not** cancel the request — the API offers asking and reading and
   * nothing else, so a button claiming to cancel generation would promise what the server cannot honour.
   */
  stopChecking(): void {
    this.watching.set(false);
  }

  onSectionActivated(item: CpAnchorNavItem): void {
    this.activeSection.set(item.targetId);

    // The nav neither scrolls nor moves focus, by design — it emits and the consumer decides. The heading is
    // what should receive focus, so a keyboard or screen-reader user lands on the section's name rather than
    // somewhere in the middle of it.
    //
    // Scoped to this host rather than `document`: these ids are not instance-namespaced, so a second mounted
    // instance would otherwise steal the target. And no `behavior` is passed — a behavior given to
    // `scrollIntoView` overrides CSS `scroll-behavior`, which is how global.css honours
    // `prefers-reduced-motion`. Letting the stylesheet decide is what keeps that promise.
    const heading = this.elementRef.nativeElement.querySelector<HTMLElement>(`#${CSS.escape(item.targetId)}`);
    heading?.scrollIntoView({ block: 'start' });
    heading?.focus({ preventScroll: true });
  }

  // -------------------------------------------------------------------------
  // Template helpers
  // -------------------------------------------------------------------------

  readonly notSupplied = NOT_SUPPLIED;

  /**
   * What a flag is called to a creator.
   *
   * The same vocabulary the proposal panel uses, so an assumption reads the same wherever a creator meets
   * one. None of these is a safety verdict: an unflagged draft has not been found safe, it has simply not
   * been flagged (ai.md), which the template says in words beside the list.
   */
  warningLabel(kind: AiWarningKind): string {
    return WARNING_LABELS[kind];
  }

  ingredientLabel(line: RecipeDraftIngredient): string {
    return line.displayText.length > 0 ? line.displayText : 'this ingredient line';
  }

  stepLabel(step: RecipeDraftStep, index: number): string {
    return `Step ${index + 1}`;
  }

  equipmentLabel(item: RecipeDraftEquipment): string {
    return item.displayText.length > 0 ? item.displayText : 'this equipment';
  }

  groupHeading<TItem>(group: RecipeDraftGroup<TItem>, index: number, total: number): string | null {
    // An untitled group among several still needs telling apart; a lone untitled group is the ordinary
    // ungrouped case and gets no heading at all (recipes.md: grouping is opt-in).
    if (group.title !== null) return group.title;
    return total > 1 ? `Group ${index + 1}` : null;
  }

  // -------------------------------------------------------------------------
  // URL resume
  // -------------------------------------------------------------------------

  private restoreFromUrl(): void {
    const requestId = this.route.snapshot.queryParamMap.get('request');
    if (requestId !== null && requestId.length > 0) this.requestIdSignal.set(requestId);
  }

  /** Written with `replaceUrl`, so polling is resumable after a refresh without cluttering browser history. */
  private writeUrl(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams: { request: this.requestIdSignal() },
    });
  }

  /** Starts this page over on a different request. Used by a host and by the discarded state's own action. */
  watchRequest(requestId: string | null): void {
    this.requestIdSignal.set(requestId);
    this.operationSignal.set(null);
    this.watchProblem.set(null);
    this.wasDiscarded.set(false);
    this.editsSignal.set(new Map());
    this.inProgressSignal.set(new Map());
    this.editingKey.set(null);
    this.editDraft.set('');
    this.editError.set('');
    this.consecutiveFailures.set(0);
    this.watching.set(true);
    this.pollStopped.set(false);
    this.writeUrl();
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }

    throw new Error('RecipeFirstDraftReviewComponent route is missing a workspaceSlug segment.');
  }
}

/** What an unsupplied value reads as. Never a blank: absent and empty must not look the same. */
const NOT_SUPPLIED = 'Not suggested';

/** Mirrors `AiProposalPanelComponent`'s own labels, so one vocabulary covers every AI surface. */
const WARNING_LABELS: Readonly<Record<AiWarningKind, string>> = {
  Unspecified: 'Note',
  Assumption: 'Assumed',
  CulinaryCaution: 'Worth your judgement',
  NonScalableLanguage: "Doesn't scale or convert",
  UnverifiedClaim: 'Not verified',
  SafetyCaution: 'Check this yourself',
  UnresolvedQuestion: 'Needs your answer',
};
