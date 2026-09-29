import { ComponentFixture, TestBed } from '@angular/core/testing';

import { AiProposalStatus, AiProposedChange, AiProposalWarning } from '../../models/ai-proposal.models';
import { AiAdvisoryResultsComponent } from './ai-advisory-results.component';

function change(partial: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'IngredientSubstitution',
    targetId: null,
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...partial,
  };
}

function warning(partial: Partial<AiProposalWarning>): AiProposalWarning {
  return { kind: 'Assumption', message: 'A note.', changeId: null, ...partial };
}

function operation(
  changes: readonly AiProposedChange[],
  warnings: readonly AiProposalWarning[] = [],
): AiProposalStatus {
  return {
    aiProposalRequestId: 'op1',
    status: 'Proposed',
    taskType: 'IngredientSubstitution',
    scope: 'Advisory',
    sourceVersionId: 'v1',
    requestedAt: '2026-09-28T14:00:00Z',
    statusChangedAt: '2026-09-28T14:01:00Z',
    failureCategory: null,
    proposal: {
      proposalId: 'p1',
      outputSchemaVersion: 'recipe.substitution.v1',
      promptTemplateId: 'recipe.substitution',
      promptTemplateVersion: '1.0.0',
      promptTemplateBodyChecksum: 'sha256:abc',
      providerName: 'test-provider',
      modelName: 'test-model',
      createdAt: '2026-09-28T14:01:00Z',
      changes,
      warnings,
    },
  };
}

describe('AiAdvisoryResultsComponent', () => {
  let fixture: ComponentFixture<AiAdvisoryResultsComponent>;

  function render(op: AiProposalStatus | null): HTMLElement {
    TestBed.configureTestingModule({});
    fixture = TestBed.createComponent(AiAdvisoryResultsComponent);
    fixture.componentRef.setInput('operation', op);
    fixture.componentRef.setInput('heading', 'Substitution advice');
    fixture.componentRef.setInput('itemNoun', 'substitution');
    fixture.componentRef.setInput('itemNounPlural', 'substitutions');
    fixture.componentRef.setInput('fieldLabels', { functionalRole: 'What it does in this recipe' });
    fixture.componentRef.setInput('enumValueFields', new Set(['confidence']));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('groups an Add row with the Set rows that follow it into one item', () => {
    const root = render(
      operation([
        change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'soured milk' }),
        change({ changeId: 's1', changeKind: 'Set', targetId: 't1', fieldName: 'functionalRole', afterValue: 'the acid' }),
        change({ changeId: 's2', changeKind: 'Set', targetId: 't1', fieldName: 'confidence', afterValue: 'Moderate' }),
      ]),
    );

    expect(fixture.componentInstance.items().length).toBe(1);
    const item = fixture.componentInstance.items()[0];
    expect(item.summary).toBe('soured milk');
    expect(item.fields).toEqual([
      { key: 'functionalRole', label: 'What it does in this recipe', value: 'the acid' },
      { key: 'confidence', label: 'Confidence', value: 'Moderate' },
    ]);
    expect(root.querySelector('.advisory-item-summary')?.textContent).toContain('soured milk');
  });

  it('humanises only the fields declared as enum-valued', () => {
    render(
      operation([
        change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'soured milk' }),
        change({ changeId: 's1', changeKind: 'Set', targetId: 't1', fieldName: 'confidence', afterValue: 'Moderate' }),
        change({
          changeId: 's2',
          changeKind: 'Set',
          targetId: 't1',
          fieldName: 'quantityGuidance',
          afterValue: 'the same volume, soured with lemon juice',
        }),
      ]),
    );

    const item = fixture.componentInstance.items()[0];
    // Already words, and must not be re-cased — humanising it would lowercase every word after the first.
    expect(item.fields.find((field) => field.key === 'quantityGuidance')?.value).toBe(
      'the same volume, soured with lemon juice',
    );
    // A raw enum member, humanised for a reader.
    expect(item.fields.find((field) => field.key === 'confidence')?.value).toBe('Moderate');
  });

  it('falls back to a humanised label for a field with no declared label', () => {
    render(
      operation([
        change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'x' }),
        change({ changeId: 's1', changeKind: 'Set', targetId: 't1', fieldName: 'evidenceBasis', afterValue: 'v' }),
      ]),
    );

    // Not in the fieldLabels map this test supplied, so it falls back to a humanised form of the name.
    expect(fixture.componentInstance.items()[0].fields[0].label).toBe('Evidence basis');
  });

  it('attaches a warning to the item it names, by the Add row it points at', () => {
    render(
      operation(
        [
          change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'almond milk' }),
          change({ changeId: 'a2', changeKind: 'Add', targetId: 't2', afterValue: 'soy milk' }),
        ],
        [warning({ kind: 'SafetyCaution', message: 'Introduces tree nuts.', changeId: 'a1' })],
      ),
    );

    const items = fixture.componentInstance.items();
    const withCaution = items.find((item) => item.summary === 'almond milk')!;
    const without = items.find((item) => item.summary === 'soy milk')!;

    expect(withCaution.warnings.length).toBe(1);
    expect(withCaution.hasSafetyCaution).toBe(true);
    expect(without.warnings.length).toBe(0);
    expect(without.hasSafetyCaution).toBe(false);
  });

  it('keeps a warning about the whole answer out of every item, and lists it separately', () => {
    const root = render(
      operation(
        [change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'almond milk' })],
        [warning({ kind: 'Assumption', message: 'Assumed a Western pantry.', changeId: null })],
      ),
    );

    expect(fixture.componentInstance.items()[0].warnings.length).toBe(0);
    expect(fixture.componentInstance.generalWarnings().length).toBe(1);
    expect(root.textContent).toContain('Assumed a Western pantry.');
  });

  it('shows an empty state, not an empty list, when nothing was proposed', () => {
    const root = render(
      operation([], [warning({ kind: 'Limitation', message: 'No safe alternative exists.', changeId: null })]),
    );

    expect(fixture.componentInstance.items().length).toBe(0);
    expect(root.querySelector('cp-empty-state')).not.toBeNull();
    expect(root.querySelector('.advisory-items')).toBeNull();
  });

  it('renders nothing about a proposal before the operation has reached Proposed', () => {
    const root = render({
      aiProposalRequestId: 'op1',
      status: 'Running',
      taskType: 'IngredientSubstitution',
      scope: 'Advisory',
      sourceVersionId: 'v1',
      requestedAt: '2026-09-28T14:00:00Z',
      statusChangedAt: '2026-09-28T14:01:00Z',
      failureCategory: null,
      proposal: null,
    });

    expect(fixture.componentInstance.hasProposal()).toBe(false);
    expect(root.querySelector('.advisory-items')).toBeNull();
    expect(root.querySelector('cp-empty-state')).toBeNull();
  });

  it('never renders anything before the first read arrives', () => {
    const root = render(null);

    expect(root.querySelector('cp-ai-operation-status')).toBeNull();
  });

  it('names the section for assistive technology', () => {
    const root = render(operation([]));

    const section = root.querySelector('section')!;
    const headingId = section.getAttribute('aria-labelledby')!;
    expect(document.getElementById(headingId)?.textContent).toContain('Substitution advice');
  });

  // ---- the results frame -----------------------------------------------------------------------------

  it('reads its results through the shared list frame, which owns the heading', () => {
    const root = render(operation([change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'oat milk' })]));

    const shell = root.querySelector('cp-list-shell');
    expect(shell).not.toBeNull();
    expect(shell?.querySelector('h2')?.textContent).toContain('Substitution advice');
    expect(fixture.componentInstance.listState()).toBe('ready');
  });

  it('is still loading while the operation is running, rather than empty', () => {
    render({
      aiProposalRequestId: 'op1',
      status: 'Running',
      taskType: 'IngredientSubstitution',
      scope: 'Advisory',
      sourceVersionId: 'v1',
      requestedAt: '2026-09-28T14:00:00Z',
      statusChangedAt: '2026-09-28T14:01:00Z',
      failureCategory: null,
      proposal: null,
    });

    expect(fixture.componentInstance.listState()).toBe('loading');
  });

  it('reports a request that did not finish as an error, never as "nothing to say"', () => {
    const root = render({
      aiProposalRequestId: 'op1',
      status: 'Failed',
      taskType: 'IngredientSubstitution',
      scope: 'Advisory',
      sourceVersionId: 'v1',
      requestedAt: '2026-09-28T14:00:00Z',
      statusChangedAt: '2026-09-28T14:01:00Z',
      failureCategory: 'Provider',
      proposal: null,
    });

    expect(fixture.componentInstance.listState()).toBe('error');
    expect(root.querySelector('cp-list-shell')?.textContent).toContain("didn't finish");
    expect(root.querySelector('cp-empty-state')).toBeNull();
  });
});
