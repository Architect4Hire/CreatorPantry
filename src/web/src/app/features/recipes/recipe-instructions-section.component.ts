import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
} from '@creator-pantry/ui';

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
    CpAnchorNavComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
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
    host.querySelector<HTMLElement>(`#group-title-${groupKey}`)?.focus();
  }

  /**
   * Adds a blank step and takes the creator to it: scrolled into view, its text field focused. The click was a
   * request to write the next step, and leaving focus on the button would mean finding the step and clicking
   * into it as well. See the ingredient editor's `addRow`.
   */
  addStep(groupKey?: string): void {
    const stepKey = this.form.addInstructionStep(groupKey);
    this.revealAndFocus(`step-text-${stepKey}`);
  }

  /** Adds a phase and focuses its heading field, which is the first thing a new phase needs. */
  addGroup(): void {
    const groupKey = this.form.addInstructionGroup();
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
