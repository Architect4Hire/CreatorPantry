import { TestBed } from '@angular/core/testing';

import { ReferenceService } from '../../services/reference.service';
import { RecipeFormState } from './recipe-form-state';

function createForm(): RecipeFormState {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      RecipeFormState,
      // The yield-unit picker reads the unit catalogue when the form is made; none of these tests is about it.
      { provide: ReferenceService, useValue: { listUnits: () => Promise.resolve({ status: 'unavailable' }) } },
    ],
  });

  return TestBed.inject(RecipeFormState);
}

describe('RecipeFormState', () => {
  describe('dirty state', () => {
    it('is clean until something differs from the baseline, and clean again once re-baselined', () => {
      const form = createForm();
      form.markClean();
      expect(form.isDirty()).toBeFalse();

      form.title.set('Banana bread');
      expect(form.isDirty()).toBeTrue();

      form.markClean();
      expect(form.isDirty()).toBeFalse();
    });

    it('counts a tag that was typed and not yet added, because it would be lost on leaving', () => {
      const form = createForm();
      form.markClean();

      form.newTagText.set('weeknight');
      expect(form.isDirty()).toBeTrue();

      form.addTag();
      expect(form.tags()).toEqual(['weeknight']);
      expect(form.newTagText()).toBe('');
    });

    it('does not add a tag twice, whatever its case', () => {
      const form = createForm();

      form.newTagText.set('Vegan');
      form.addTag();
      form.newTagText.set('vegan');
      form.addTag();

      expect(form.tags()).toEqual(['Vegan']);
    });

    it('counts an added group as a change, but sends no step that was never filled in', () => {
      const form = createForm();
      form.markClean();

      form.addInstructionStep();

      // The first step of a new method creates the group that holds it, and a group is always submitted.
      expect(form.instructionGroups().length).toBe(1);
      expect(form.instructionGroups()[0].steps.length).toBe(1);
      expect(form.isDirty()).toBeTrue();

      // The empty step inside it is not sent, so it is not a change of its own: dirty state is measured on the
      // request, which is what a save would send.
      expect(form.buildCreateRequest().instructions![0].steps).toEqual([]);
    });
  });

  describe('total time', () => {
    it('adds prep, cook and rest, and is null when none is recorded', () => {
      const form = createForm();
      expect(form.totalTimeMinutes()).toBeNull();

      form.prepTimeMinutes.set(15);
      form.restTimeMinutes.set(0);
      form.cookTimeMinutes.set(30);

      expect(form.totalTimeMinutes()).toBe(45);
      expect(form.timingSum()?.parts).toEqual(['prep 15', 'cook 30', 'rest 0']);
    });

    it('reports a sum past the longest recordable time instead of recording it', () => {
      const form = createForm();

      form.prepTimeMinutes.set(525_600);
      form.cookTimeMinutes.set(1);

      expect(form.totalTimeMinutes()).toBeNull();
      expect(form.totalTimeOverflowNote()).toContain('prep 525600 + cook 1');
    });
  });

  describe('the measured batch', () => {
    it('is open when the recipe records any part of it, and stays open once asked for', () => {
      const form = createForm();
      expect(form.measuredBatchOpen()).toBeFalse();

      form.servingSize.set(250);
      expect(form.measuredBatchOpen()).toBeTrue();

      form.servingSize.set(null);
      expect(form.measuredBatchOpen()).toBeFalse();

      form.measuredBatchRevealed.set(true);
      expect(form.measuredBatchOpen()).toBeTrue();
    });

    it('clears the batch, its refusals and the latch, and leaves the creator’s own wording and the serving count', () => {
      const form = createForm();
      form.yieldText.set('Makes 2 loaves, serves 12');
      form.servingCount.set(12);
      form.yieldQuantity.set(2);
      form.yieldUnitId.set('unit-loaf');
      form.servingSize.set(0.5);
      form.measuredBatchRevealed.set(true);
      form.fieldErrors.set({ yieldQuantity: ['Too many.'], servingSize: ['Too big.'], title: ['Needs a name.'] });

      expect(form.holdsMeasuredBatchValues()).toBeTrue();
      form.clearMeasuredBatch();

      expect(form.holdsMeasuredBatchValues()).toBeFalse();
      expect(form.yieldUnitText()).toBe('');
      expect(form.measuredBatchOpen()).toBeFalse();
      expect(form.yieldText()).toBe('Makes 2 loaves, serves 12');
      expect(form.servingCount()).toBe(12);
      // Only the refusals about fields that went; one about something else is still true.
      expect(form.fieldErrors()).toEqual({ title: ['Needs a name.'] });
    });
  });

  describe('what a save submits', () => {
    it('trims the title, sends blank text as null and carries the total the parts add up to', () => {
      const form = createForm();
      form.title.set('  Banana bread  ');
      form.description.set('   ');
      form.notes.set(' Better the next day. ');
      form.prepTimeMinutes.set(10);
      form.cookTimeMinutes.set(50);

      const request = form.buildCreateRequest();

      expect(request.title).toBe('Banana bread');
      expect(request.description).toBeNull();
      expect(request.notes).toBe('Better the next day.');
      expect(request.totalTimeMinutes).toBe(60);
    });

    it('marks every field of an update as submitted, quoting the token it was given', () => {
      const form = createForm();
      form.title.set('Soup');

      const request = form.buildUpdateRequest('token-1');

      expect(request.expectedConcurrencyToken).toBe('token-1');
      expect(request.title).toEqual({ submitted: true, value: 'Soup' });
      expect(request.description).toEqual({ submitted: true, value: null });
    });
  });

  describe('the instruction list', () => {
    it('adds, moves and removes steps and groups by key', () => {
      const form = createForm();

      form.addInstructionStep();
      form.addInstructionStep();
      const [group] = form.instructionGroups();
      const [first, second] = group.steps;
      form.updateInstructionStep(group.key, first.key, { text: 'Mix.' });
      form.updateInstructionStep(group.key, second.key, { text: 'Bake.' });

      form.moveInstructionStep(group.key, second.key, -1);
      expect(form.instructionGroups()[0].steps.map((step) => step.text)).toEqual(['Bake.', 'Mix.']);

      // A move off either end does nothing.
      form.moveInstructionStep(group.key, second.key, -1);
      expect(form.instructionGroups()[0].steps.map((step) => step.text)).toEqual(['Bake.', 'Mix.']);

      form.removeInstructionStep(group.key, first.key);
      expect(form.instructionGroups()[0].steps.map((step) => step.text)).toEqual(['Bake.']);

      form.addInstructionGroup();
      expect(form.showInstructionGroups()).toBeTrue();
      expect(form.instructionGroups().length).toBe(2);

      form.removeInstructionGroup(form.instructionGroups()[1].key);
      expect(form.instructionGroups().length).toBe(1);
    });

    it('reports a refused step against the step it was about, by the position it was sent in', () => {
      const form = createForm();
      form.addInstructionStep();
      const [group] = form.instructionGroups();
      form.addInstructionStep(group.key);
      const [blank, written] = form.instructionGroups()[0].steps;
      form.updateInstructionStep(group.key, written.key, { text: 'Bake.' });

      // The blank step is not sent, so position 0 on the wire is the written one.
      form.fieldErrors.set({ 'instructions[0].steps[0].text': ['Too short.'] });

      expect(form.stepProblem(written.key)).toBe('Too short.');
      expect(form.stepProblem(blank.key)).toBe('');
    });
  });

  describe('field errors', () => {
    it('reads the first message for a field, and nothing once cleared', () => {
      const form = createForm();
      form.fieldErrors.set({ title: ['Needs a name.', 'Too short.'] });

      expect(form.fieldError('title')).toBe('Needs a name.');
      expect(form.fieldError('description')).toBe('');

      form.fieldErrors.set({});
      expect(form.fieldError('title')).toBe('');
    });
  });
});
