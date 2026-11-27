import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { FormsModule } from '@angular/forms';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpIconComponent,
  CpRuledListComponent,
  CpSheetComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { GroupedPlace, handleMoveFor, handleTarget } from './recipe-editor.model';
import { RecipeFormState } from './recipe-form-state';

/**
 * The id prefix of an instruction group's own anchor. Its ingredient counterpart lives in the ingredient
 * editor: each owns the ids it renders, and `CpAnchorNavComponent` only carries them back.
 */
const INSTRUCTION_GROUP_ANCHOR_PREFIX = 'instruction-group-';

/**
 * The method: its steps, optionally in phases, with the nav down the side that appears once there is more than
 * one.
 *
 * The steps and groups are the form's, and every edit goes through it, so a step edited in a group scrolled out
 * of view is dirty state like any other. What is here is what only a drawn list can know: the nav, and where
 * focus goes when a duration is asked for.
 */
@Component({
  selector: 'cp-recipe-instructions-section',
  standalone: true,
  imports: [
    CdkDrag,
    CdkDragHandle,
    CdkDropList,
    CdkDropListGroup,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpIconComponent,
    CpRuledListComponent,
    CpSheetComponent,
    FormsModule,
  ],
  templateUrl: './recipe-instructions-section.component.html',
  styleUrl: './recipe-instructions-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeInstructionsSectionComponent {
  protected readonly form = inject(RecipeFormState);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);
  private readonly confirmer = inject(ConfirmService);

  /**
   * The instruction groups, named and counted for the nav down the side of the Instructions tab.
   *
   * Every group stays rendered — the nav scrolls, it does not mount — so a step edited in a group scrolled out
   * of view is in `instructionGroups()` like any other, and dirty-state and the save see it.
   */
  readonly instructionGroupNav = computed<readonly CpAnchorNavItem[]>(() =>
    this.form.instructionGroups().map((group, index) => ({
      targetId: INSTRUCTION_GROUP_ANCHOR_PREFIX + group.key,
      label: group.title.trim() || `Group ${index + 1} (untitled)`,
      detail: `${group.steps.length} ${group.steps.length === 1 ? 'step' : 'steps'}`,
    })),
  );

  /**
   * Shown only for a method with phases to move between — the same rule the ingredient list follows. One group
   * is not navigation, and a method with no phases has nothing to navigate.
   */
  readonly showInstructionGroupNav = computed(
    () => this.form.showInstructionGroups() && this.form.instructionGroups().length > 1,
  );

  /** The group last jumped to, which `aria-current` names. See the ingredient editor's `activeGroupKey`. */
  readonly activeInstructionGroupKey = signal<string | null>(null);

  readonly activeInstructionGroupTargetId = computed(() => {
    const key = this.activeInstructionGroupKey();

    return key === null ? null : INSTRUCTION_GROUP_ANCHOR_PREFIX + key;
  });

  /**
   * Jumps to the instruction group the nav named: marks it current, scrolls it into view, and moves focus to
   * its heading field — the same three things the ingredient editor does, for the same reason. The shared nav
   * does none of them, because only this section can name the control worth focusing.
   */
  onInstructionGroupNavActivated(item: CpAnchorNavItem): void {
    const groupKey = item.targetId.slice(INSTRUCTION_GROUP_ANCHOR_PREFIX.length);
    this.activeInstructionGroupKey.set(groupKey);

    const host = this.elementRef.nativeElement;
    host.querySelector(`#${item.targetId}`)?.scrollIntoView({ block: 'start' });
    // The heading field when it is showing, the heading itself when it is closed to its words.
    host.querySelector<HTMLElement>(`#group-title-${groupKey}, #group-heading-${groupKey}`)?.focus();
  }

  /**
   * Adds a blank step and takes the creator to it: scrolled into view, its text field focused. The click was a
   * request to write the next step, and leaving focus on the button would mean finding the step and clicking
   * into it as well. See the ingredient editor's `addRow`.
   */
  /**
   * The steps whose fields are showing. A method opens with every step closed to its own words, so it reads as a
   * method rather than a wall of fields; the creator opens the one to change. Which are open is this screen's alone
   * — nothing is saved or sent — and a closed step keeps every value it holds. The ingredient editor's rule.
   */
  private readonly openSteps = signal<ReadonlySet<string>>(new Set());

  /** The phases whose heading field is showing, on the same terms: closed, a phase is its heading as it reads. */
  private readonly openGroups = signal<ReadonlySet<string>>(new Set());

  constructor() {
    // A step the server refused opens, so its sentence and the field it is about are in view, never folded away
    // behind a line that reads as fine. Opened rather than held open, so the creator can still close it again.
    effect(() => {
      const refused = [...this.form.instructionStepErrors().keys()];
      untracked(() => refused.forEach((stepKey) => this.openStep(stepKey)));
    });
    effect(() => {
      const refused = [...this.form.instructionGroupErrors().keys()];
      untracked(() => refused.forEach((groupKey) => this.openGroup(groupKey)));
    });
  }

  /** Whether this step's fields are showing. */
  isStepOpen(stepKey: string): boolean {
    return this.openSteps().has(stepKey);
  }

  /** Opens a step's fields, or closes them again. Focus stays on the button, which does not move. */
  toggleStep(stepKey: string): void {
    this.openSteps.update((open) => {
      const next = new Set(open);
      if (!next.delete(stepKey)) next.add(stepKey);
      return next;
    });
  }

  /** Whether this phase's heading field is showing. */
  isGroupOpen(groupKey: string): boolean {
    return this.openGroups().has(groupKey);
  }

  /** Opens a phase's heading field, or closes it again. Focus stays on the button, which does not move. */
  toggleGroup(groupKey: string): void {
    this.openGroups.update((open) => {
      const next = new Set(open);
      if (!next.delete(groupKey)) next.add(groupKey);
      return next;
    });
  }

  private openGroup(groupKey: string): void {
    if (!this.openGroups().has(groupKey)) this.openGroups.update((open) => new Set(open).add(groupKey));
  }

  private openStep(stepKey: string): void {
    if (!this.openSteps().has(stepKey)) this.openSteps.update((open) => new Set(open).add(stepKey));
  }

  /**
   * What a closed step has beyond its words — its duration and its note — as one line, or '' when it has neither.
   * The duration is the creator's own number of minutes, shown as they entered it.
   */
  stepExtras(step: { readonly durationMinutes: number | null; readonly note: string }): string {
    const parts: string[] = [];
    if (step.durationMinutes !== null && step.durationMinutes !== undefined && String(step.durationMinutes) !== '') {
      parts.push(`${step.durationMinutes} min`);
    }
    const note = step.note.trim();
    if (note !== '') parts.push(`Note: ${note}`);

    return parts.join(' · ');
  }

  /** What the last move did, in words, for the section's polite live region: a drag or a key moves in silence. */
  protected readonly moveAnnouncement = signal('');

  /**
   * A step's move handle, from the keyboard: Up and Down move it one place — into the next phase at either end of
   * one, when the method has phases — and Home and End to the top or the bottom of its own. Focus stays on the
   * handle, which goes with the step. Any other key is the browser's.
   */
  onStepHandleKeydown(event: KeyboardEvent, stepKey: string): void {
    const move = handleMoveFor(event);
    if (move === null) return;
    event.preventDefault();

    const to = handleTarget(
      this.form.instructionGroups(),
      (group) => group.steps,
      stepKey,
      move,
      this.form.showInstructionGroups(),
    );
    if (to === null) {
      this.moveAnnouncement.set(move === 'up' || move === 'first' ? 'Already at the top.' : 'Already at the bottom.');
      return;
    }
    this.moveStep(stepKey, to);
    afterNextRender(
      () => this.elementRef.nativeElement.querySelector<HTMLElement>(`[id="step-handle-${stepKey}"]`)?.focus(),
      { injector: this.injector },
    );
  }

  /** A step dropped where it was dragged to: its own phase, or another. Each list carries its group's key. */
  onStepDropped(event: CdkDragDrop<string, string, string>): void {
    if (event.previousContainer === event.container && event.previousIndex === event.currentIndex) return;
    this.moveStep(event.item.data, { groupKey: event.container.data, index: event.currentIndex });
  }

  private moveStep(stepKey: string, to: GroupedPlace): void {
    this.form.moveInstructionStepTo(stepKey, to);

    const groups = this.form.instructionGroups();
    const groupIndex = groups.findIndex((group) => group.key === to.groupKey);
    const group = groups[groupIndex];
    if (!group) return;
    const where = this.form.showInstructionGroups() ? ` in ${group.title.trim() || `group ${groupIndex + 1}`}` : '';
    this.moveAnnouncement.set(`Moved step to position ${to.index + 1} of ${group.steps.length}${where}.`);
  }

  addStep(groupKey?: string): void {
    const stepKey = this.form.addInstructionStep(groupKey);
    // Open, since it has no words yet to show closed, and the click was a request to write it.
    this.openStep(stepKey);
    this.revealAndFocus(`step-text-${stepKey}`);
  }

  /** Adds a phase and focuses its heading field, which is the first thing a new phase needs. */
  addGroup(): void {
    const groupKey = this.form.addInstructionGroup();
    // Open, since it has no name yet to show closed, and the click was a request to name it.
    this.openGroup(groupKey);
    this.revealAndFocus(`group-title-${groupKey}`);
  }

  /**
   * Puts this step's duration field on show, and moves focus into it.
   *
   * Focus moves because the click was a request to type a number — leaving it on a button that has just
   * replaced itself strands a keyboard user in front of the field they asked for. It waits for the render that
   * creates the input, since there is nothing to focus before that.
   *
   * Reveals only; it writes no duration, so the recipe is untouched and the form does not become dirty. See
   * `EditableInstructionStep.showDuration`.
   */
  revealStepDuration(groupKey: string, stepKey: string): void {
    this.form.updateInstructionStep(groupKey, stepKey, { showDuration: true });

    afterNextRender(
      () => this.elementRef.nativeElement.querySelector<HTMLElement>(`[id="step-duration-${stepKey}"]`)?.focus(),
      { injector: this.injector },
    );
  }

  /** Puts this step's note field on show and moves focus into it, on the terms of {@link revealStepDuration}. */
  revealStepNote(groupKey: string, stepKey: string): void {
    this.form.updateInstructionStep(groupKey, stepKey, { showNote: true });

    afterNextRender(
      () => this.elementRef.nativeElement.querySelector<HTMLElement>(`[id="step-note-${stepKey}"]`)?.focus(),
      { injector: this.injector },
    );
  }

  /**
   * Takes this step's note away and puts "+ Note" back, for a step whose note is not wanted: an AI draft wrote one
   * the creator does not need, or it was opened and never filled in. The field was only ever offered, so the
   * creator can put it away as well as open it.
   *
   * A note with words in it asks first, as {@link removeStep} does, because a slip of the pointer should not cost
   * something written; an empty one goes at once. Focus goes to the offer that comes back — leaving it on a button
   * that has just removed itself would strand a keyboard user — once the render that draws it has run.
   */
  async removeStepNote(groupKey: string, stepKey: string, stepIndex: number): Promise<void> {
    const step = this.form
      .instructionGroups()
      .find((group) => group.key === groupKey)
      ?.steps.find((candidate) => candidate.key === stepKey);
    if (!step) return;

    if (step.note.trim() !== '') {
      const agreed = await this.confirmer.confirm({
        title: `Remove the note from step ${stepIndex + 1}?`,
        message: 'Its words go with it. The rest of the step stays as it is.',
        confirmLabel: 'Remove note',
        cancelLabel: 'Keep it',
        tone: 'danger',
      });
      if (!agreed) return;
    }

    this.form.updateInstructionStep(groupKey, stepKey, { note: '', showNote: false });

    afterNextRender(
      () =>
        this.elementRef.nativeElement
          .querySelector<HTMLElement>(`button[aria-label="Add a note to step ${stepIndex + 1}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  /**
   * Takes this step's duration away and puts "+ Duration" back, for a step whose duration is not wanted: an AI draft
   * wrote one the creator does not need, or it was opened and never filled in. The field was only ever offered, so
   * the creator can put it away as well as open it.
   *
   * It goes at once, with no question: a number of minutes is not words the creator wrote, and typing it again costs
   * nothing. Focus goes to the offer that comes back — leaving it on a button that has just removed itself would
   * strand a keyboard user — once the render that draws it has run.
   */
  removeStepDuration(groupKey: string, stepKey: string, stepIndex: number): void {
    this.form.updateInstructionStep(groupKey, stepKey, { durationMinutes: null, showDuration: false });

    afterNextRender(
      () =>
        this.elementRef.nativeElement
          .querySelector<HTMLElement>(`button[aria-label="Add a duration to step ${stepIndex + 1}"]`)
          ?.focus(),
      { injector: this.injector },
    );
  }

  /**
   * Removes a step. A step with words in it asks first: the control is a small ✕ beside its text now, not a
   * labelled button of its own, and a slip of the pointer should not cost a written step. An empty one goes at
   * once — there is nothing to lose, and asking would only be in the way.
   */
  async removeStep(groupKey: string, stepKey: string, stepIndex: number): Promise<void> {
    const step = this.form
      .instructionGroups()
      .find((group) => group.key === groupKey)
      ?.steps.find((candidate) => candidate.key === stepKey);
    if (!step) return;

    const written = step.text.trim() !== '' || step.note.trim() !== '';
    if (written) {
      const agreed = await this.confirmer.confirm({
        title: `Remove step ${stepIndex + 1}?`,
        message: 'Its text, and its note and duration if it has them, go with it.',
        confirmLabel: 'Remove step',
        cancelLabel: 'Keep it',
        tone: 'danger',
      });
      if (!agreed) return;
    }

    this.form.removeInstructionStep(groupKey, stepKey);
  }

  /**
   * Once the render that creates it has run, brings the step or group holding this control into view and
   * focuses the control. The whole step rather than only the field, so its Remove is on screen too; `nearest`,
   * so a step already in view does not jump.
   */
  private revealAndFocus(controlId: string): void {
    afterNextRender(
      () => {
        const control = this.elementRef.nativeElement.querySelector<HTMLElement>(`[id="${controlId}"]`);
        control?.closest('li, section')?.scrollIntoView({ block: 'nearest' });
        control?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }
}
