import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  NutritionAmount,
  NutritionEstimate,
  NutritionLineEstimate,
  NutritionMapping,
} from '../../models/nutrition.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import {
  NutritionEstimateOutcome,
  NutritionMappingDecisionOutcome,
  NutritionMappingOutcome,
  NutritionService,
} from '../../services/nutrition.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import {
  BASIS_LABELS,
  MATCH_CONFIDENCE_LABELS,
  MATCH_METHOD_LABELS,
  PORTION_ORIGIN_TEXT,
  LINE_REASON_TEXT,
  NUTRIENT_LABELS,
  OUTCOME_LABELS,
  PANEL_COPY,
  PUBLISHABLE_SENTENCE,
  RecipeNutritionComponent,
  UNRESOLVED_GROUP_HEADINGS,
  UNRESOLVED_REASON_TEXT,
  amountText,
  portionText,
  rejectedReferencesSentence,
  confidenceSentence,
  incompleteNutrientSentence,
  publishBlockedSentence,
} from './recipe-nutrition.component';

const METHOD_TEXT =
  "Each counted ingredient's nutrients per 100 g, from its confirmed reference, multiplied by its weight in " +
  'grams and summed. Quantities are taken as the edible portion as written.';

function amount(overrides: Partial<NutritionAmount> = {}): NutritionAmount {
  return {
    low: 1234.567891,
    high: 1234.567891,
    displayLow: 1235,
    displayHigh: 1235,
    displayPrecision: 0,
    rounding: 'ToEven',
    isRange: false,
    trace: false,
    ...overrides,
  };
}

function line(overrides: Partial<NutritionLineEstimate> = {}): NutritionLineEstimate {
  return {
    lineId: 'l1',
    displayText: '250 g butter',
    outcome: 'Included',
    reason: 'None',
    required: true,
    gramsLow: 250,
    gramsHigh: 250,
    basis: 'Mass',
    mappingId: 'm1',
    source: { sourceCode: 'fdc', externalRecordId: '173410', sourceVersion: '2026-01-01' },
    ...overrides,
  };
}

const UNMAPPED_LINE = line({
  lineId: 'l2',
  displayText: '1 cup oat milk',
  outcome: 'Unmapped',
  reason: 'NoMapping',
  gramsLow: null,
  gramsHigh: null,
  basis: null,
  mappingId: null,
  source: null,
});

function estimate(overrides: Partial<NutritionEstimate> = {}): NutritionEstimate {
  return {
    recipeId: 'r1',
    recipeVersionId: 'v4',
    versionNumber: 4,
    label: 'Estimate',
    methodVersion: 'nutrition-estimate/1',
    method: METHOD_TEXT,
    status: 'Incomplete',
    publishable: false,
    partial: true,
    confidence: 'CreatorPortion',
    servings: 12,
    servingsSource: 'ServingCount',
    nutrients: [
      {
        code: 'Energy',
        unit: 'Kilocalorie',
        wholeRecipe: amount(),
        perServing: amount({ low: 102.880657, displayLow: 103, displayHigh: 103 }),
        complete: true,
        linesMissingIt: [],
      },
      {
        code: 'VitaminD',
        unit: 'Microgram',
        wholeRecipe: amount({ low: 0.004, displayLow: 0, displayHigh: 0, displayPrecision: 1, trace: true }),
        perServing: amount({ low: 0.0003, displayLow: 0, displayHigh: 0, displayPrecision: 1, trace: true }),
        complete: false,
        linesMissingIt: ['l1'],
      },
    ],
    lines: [line(), UNMAPPED_LINE],
    sources: [
      {
        sourceCode: 'fdc',
        citation: 'USDA FoodData Central',
        licenseCode: 'CC0-1.0',
        licenseUrl: 'https://example.test/cc0',
      },
    ],
    unresolved: { unmapped: ['l2'], needsReview: [], unconvertible: [], notQuantified: [] },
    ...overrides,
  };
}

function mapping(overrides: Partial<NutritionMapping> = {}): NutritionMapping {
  return {
    id: 'm2',
    ingredientId: 'i2',
    status: 'Candidate',
    matchMethod: 'AutomaticSearch',
    confidence: 'Low',
    reference: {
      sourceCode: 'fdc',
      externalRecordId: '174832',
      description: 'Oat milk, unsweetened',
      sourceVersion: '2026-02-01',
    },
    reviewSourceVersion: null,
    unresolvedReason: null,
    portion: { measurementUnitId: 'u1', amount: 1, grams: 240, label: 'cup', origin: 'Reference' },
    rejectedReferenceCount: 2,
    confirmedAt: null,
    updatedAt: '2026-10-19T09:00:00Z',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

/** The saved recipe: where a line's platform ingredient id comes from. */
const SAVED_GROUPS: readonly RecipeIngredientGroup[] = [
  {
    id: 'g1',
    title: null,
    sortOrder: 0,
    ingredients: [
      {
        id: 'l1',
        sortOrder: 0,
        displayText: '250 g butter',
        displayTextSource: 'Creator',
        ingredientNameText: 'butter',
        unitText: 'g',
        quantity: 250,
        quantityUpper: null,
        measurementUnitId: 'u-gram',
        ingredientId: 'i1',
        matchStatus: 'Matched',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'Proportional',
      },
      {
        id: 'l2',
        sortOrder: 1,
        displayText: '1 cup oat milk',
        displayTextSource: 'Creator',
        ingredientNameText: 'oat milk',
        unitText: 'cup',
        quantity: 1,
        quantityUpper: null,
        measurementUnitId: 'u-cup',
        ingredientId: 'i2',
        matchStatus: 'Matched',
        preparationNote: null,
        isOptional: false,
        scalingBehavior: 'Proportional',
      },
    ],
  },
];

interface EstimateCall {
  readonly workspaceSlug: string;
  readonly recipeId: string;
  readonly versionNumber: number;
  readonly servings: number | null;
}

interface DecisionCall {
  readonly operation: 'confirm' | 'reject';
  readonly ingredientId: string;
  readonly token: string;
}

class StubNutritionService {
  estimateOutcome: NutritionEstimateOutcome = { status: 'estimated', estimate: estimate() };
  mappingOutcome: NutritionMappingOutcome = { status: 'found', mapping: mapping() };
  decisionOutcome: NutritionMappingDecisionOutcome = {
    status: 'decided',
    mapping: mapping({ status: 'Confirmed', confirmedAt: '2026-10-20T10:00:00Z', concurrencyToken: 'token-2' }),
  };

  estimateCalls: EstimateCall[] = [];
  mappingCalls: string[] = [];
  decisionCalls: DecisionCall[] = [];

  holdNextEstimate = false;
  private release: (() => void) | null = null;

  estimate(
    workspaceSlug: string,
    recipeId: string,
    versionNumber: number,
    request: { servings: number | null },
  ): Promise<NutritionEstimateOutcome> {
    this.estimateCalls.push({ workspaceSlug, recipeId, versionNumber, servings: request.servings });

    if (!this.holdNextEstimate) return Promise.resolve(this.estimateOutcome);

    this.holdNextEstimate = false;
    return new Promise<NutritionEstimateOutcome>((resolve) => {
      this.release = () => resolve(this.estimateOutcome);
    });
  }

  finish(): void {
    this.release?.();
    this.release = null;
  }

  getMapping(_workspaceSlug: string, ingredientId: string): Promise<NutritionMappingOutcome> {
    this.mappingCalls.push(ingredientId);
    return Promise.resolve(this.mappingOutcome);
  }

  confirmMapping(
    _workspaceSlug: string,
    ingredientId: string,
    decision: { expectedConcurrencyToken: string },
  ): Promise<NutritionMappingDecisionOutcome> {
    this.decisionCalls.push({ operation: 'confirm', ingredientId, token: decision.expectedConcurrencyToken });
    return Promise.resolve(this.decisionOutcome);
  }

  rejectMapping(
    _workspaceSlug: string,
    ingredientId: string,
    decision: { expectedConcurrencyToken: string },
  ): Promise<NutritionMappingDecisionOutcome> {
    this.decisionCalls.push({ operation: 'reject', ingredientId, token: decision.expectedConcurrencyToken });
    return Promise.resolve(this.decisionOutcome);
  }
}

function membership(role: WorkspaceRole): MyWorkspaceMembership {
  return {
    workspaceId: 'w1',
    workspaceSlug: 'cozy-fall',
    workspaceName: 'Cozy Fall',
    membershipId: 'm1',
    role,
    status: 'Active',
  };
}

class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Editor')] });

  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }

  setRole(role: WorkspaceRole): void {
    this.state.set({ status: 'ready', memberships: [membership(role)] });
  }
}

class StubConfirmService {
  answer = true;
  requests: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.requests.push(request);
    return Promise.resolve(this.answer);
  }
}

describe('RecipeNutritionComponent', () => {
  let service: StubNutritionService;
  let memberships: StubMembershipService;
  let confirmService: StubConfirmService;
  let fixture: ComponentFixture<RecipeNutritionComponent>;

  async function render(versionNumber: number | null = 4, dirty = false): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeNutritionComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('sourceVersionNumber', versionNumber);
    fixture.componentRef.setInput('savedIngredientGroups', SAVED_GROUPS);
    fixture.componentRef.setInput('editorIsDirty', dirty);

    fixture.detectChanges();
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function text(element: HTMLElement): string {
    return element.textContent ?? '';
  }

  function buttonWith(element: HTMLElement, label: string): HTMLButtonElement | undefined {
    return Array.from(element.querySelectorAll('button')).find((button) =>
      button.textContent?.trim().includes(label),
    );
  }

  async function click(element: HTMLElement, label: string): Promise<void> {
    buttonWith(element, label)?.click();
    await settle();
  }

  async function estimated(element: HTMLElement): Promise<void> {
    await click(element, PANEL_COPY.submit);
  }

  /** Opens the mapping review on the one unmapped line. */
  async function openMapping(element: HTMLElement): Promise<void> {
    await estimated(element);
    await click(element, PANEL_COPY.reviewMapping);
  }

  beforeEach(() => {
    service = new StubNutritionService();
    memberships = new StubMembershipService();
    confirmService = new StubConfirmService();

    TestBed.configureTestingModule({
      imports: [RecipeNutritionComponent],
      providers: [
        { provide: NutritionService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        { provide: ConfirmService, useValue: confirmService },
      ],
    });
  });

  // ---- Asking -----------------------------------------------------------

  it('says what an estimate is, and is not, before any figure appears', async () => {
    const element = await render();

    expect(text(element)).toContain(PANEL_COPY.intro);
    expect(text(element)).toContain(PANEL_COPY.notALabel);
    expect(service.estimateCalls.length).withContext('nothing is read until asked').toBe(0);
  });

  it('will not ask about a recipe with no saved version, and says that instead', async () => {
    const element = await render(null);

    expect(text(element)).toContain(PANEL_COPY.noVersion);
    expect(buttonWith(element, PANEL_COPY.submit)?.disabled).toBeTrue();
  });

  it('asks for the saved version, with the version’s own basis when the box is empty', async () => {
    const element = await render();
    await estimated(element);

    expect(service.estimateCalls).toEqual([
      { workspaceSlug: 'cozy-fall', recipeId: 'r1', versionNumber: 4, servings: null },
    ]);
  });

  it('re-asks the server for a different serving basis rather than dividing anything itself', async () => {
    const element = await render();
    await estimated(element);

    const box = element.querySelector('input[type="number"]') as HTMLInputElement;
    box.value = '24';
    box.dispatchEvent(new Event('input'));
    await settle();
    await click(element, PANEL_COPY.recheck);

    expect(service.estimateCalls.length).toBe(2);
    expect(service.estimateCalls[1].servings).toBe(24);
  });

  it('refuses a serving box that holds no number, before any request is made', async () => {
    const element = await render();
    fixture.componentInstance.servingsValue.set(Number.NaN);
    await click(element, PANEL_COPY.submit);

    expect(service.estimateCalls.length).toBe(0);
    expect(text(element)).toContain(PANEL_COPY.servingsLocalError);
  });

  it('says that unsaved edits are not in the figures', async () => {
    const element = await render(4, true);

    expect(text(element)).toContain(PANEL_COPY.dirtyPill);
    expect(text(element)).toContain(PANEL_COPY.dirty);
  });

  it('says a request is in flight and keeps the button focusable while it is', async () => {
    const element = await render();
    const submit = buttonWith(element, PANEL_COPY.submit)!;
    submit.focus();
    service.holdNextEstimate = true;
    submit.click();
    await settle();

    const working = buttonWith(element, PANEL_COPY.working)!;
    expect(working.disabled).toBeFalse();
    expect(working.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(working);
    expect(element.querySelector('[role="status"].cp-sr-only')?.textContent).toContain(PANEL_COPY.checking);

    working.click();
    await settle();
    expect(service.estimateCalls.length).toBe(1);

    service.finish();
    await settle();
    expect(text(element)).toContain(NUTRIENT_LABELS.Energy);
  });

  // ---- The figures ------------------------------------------------------

  it('renders the server’s own label, method version and display figures, with no arithmetic of its own', async () => {
    const element = await render();
    await estimated(element);

    expect(text(element)).toContain('Estimate');
    expect(text(element)).toContain('nutrition-estimate/1');
    expect(text(element)).toContain('Read against version 4');

    const figures = Array.from(element.querySelectorAll('.figure')).map((cell) => cell.textContent?.trim());
    // 1235 and 103 are the server's `displayLow`; nothing here divided 1235 by 12 to get the second.
    expect(figures).toContain('1235 kcal');
    expect(figures).toContain('103 kcal');
  });

  it('shows a value that rounds to zero as a trace, never as 0', async () => {
    const element = await render();
    await estimated(element);

    const row = Array.from(element.querySelectorAll('tbody tr')).find((candidate) =>
      candidate.textContent?.includes(NUTRIENT_LABELS.VitaminD),
    );
    expect(row?.textContent).toContain(PANEL_COPY.traceText);
    expect(row?.textContent).not.toMatch(/\b0(\.0)? µg/);
  });

  it('shows a range as a range', async () => {
    service.estimateOutcome = {
      status: 'estimated',
      estimate: estimate({
        nutrients: [
          {
            code: 'Protein',
            unit: 'Gram',
            wholeRecipe: amount({
              low: 10,
              high: 12,
              displayLow: 10,
              displayHigh: 12,
              displayPrecision: 1,
              isRange: true,
            }),
            perServing: null,
            complete: true,
            linesMissingIt: [],
          },
        ],
      }),
    };

    const element = await render();
    await estimated(element);

    expect(text(element)).toContain('10.0 to 12.0 g');
  });

  it('keeps an incomplete nutrient visible, with the gap named rather than the figure dropped', async () => {
    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(NUTRIENT_LABELS.VitaminD);
    expect(text(element)).toContain('Incomplete');
    expect(text(element)).toContain(incompleteNutrientSentence(1));
  });

  it('shows a partial estimate’s totals and says they are a lower bound', async () => {
    const element = await render();
    await estimated(element);

    expect(text(element)).toContain('1235 kcal');
    expect(text(element)).toContain('lower bound');
  });

  it('says how the weights were known, at the weakest basis among the counted lines', async () => {
    const element = await render();
    await estimated(element);

    // Said as the weakest basis across the counted lines, which is what the server's `confidence` is — and
    // never as a precision claim about weighing, which an earlier draft of this sentence read as.
    expect(text(element)).toContain(confidenceSentence('CreatorPortion'));
    expect(confidenceSentence('Mass')).not.toContain('least precise');
  });

  it('says where the serving count came from', async () => {
    const element = await render();
    await estimated(element);

    expect(text(element)).toContain("this recipe's own serving count");
  });

  it('gives no per-serving column when the version records no servings, and says why', async () => {
    service.estimateOutcome = {
      status: 'estimated',
      estimate: estimate({ servings: null, servingsSource: 'None' }),
    };

    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(PANEL_COPY.noServings);
    const headers = Array.from(element.querySelectorAll('thead th')).map((cell) => cell.textContent?.trim());
    expect(headers).not.toContain(PANEL_COPY.perServingColumn);
  });

  it('shows the exact figures on request, and never uses them to round', async () => {
    const element = await render();
    await estimated(element);

    await click(element, PANEL_COPY.exactHeading);

    expect(text(element)).toContain('1234.567891');
  });

  it('shows the server’s own description of the method on request', async () => {
    const element = await render();
    await estimated(element);

    await click(element, PANEL_COPY.methodHeading);

    expect(element.querySelector('.method-text')?.textContent?.trim()).toBe(METHOD_TEXT);
  });

  it('cites every source with its licence', async () => {
    const element = await render();
    await estimated(element);

    const sources = element.querySelector('.sources');
    expect(sources?.textContent).toContain('USDA FoodData Central');
    expect(sources?.textContent).toContain('CC0-1.0');
    expect(sources?.querySelector('a')?.getAttribute('href')).toBe('https://example.test/cc0');
  });

  // ---- Publishing -------------------------------------------------------

  it('says not to publish a partial estimate, counting the ingredients in the way', async () => {
    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(publishBlockedSentence(1));
  });

  it('states the publishing rule for a complete estimate without granting permission', async () => {
    service.estimateOutcome = {
      status: 'estimated',
      estimate: estimate({
        status: 'Complete',
        publishable: true,
        partial: false,
        lines: [line()],
        unresolved: { unmapped: [], needsReview: [], unconvertible: [], notQuantified: [] },
      }),
    };

    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(PUBLISHABLE_SENTENCE);
    // The rule, and who checks it — never "these figures may be published", which reads as permission.
    expect(text(element)).toContain('the call is yours');
    expect(PUBLISHABLE_SENTENCE).not.toContain('may be published');
  });

  // ---- What was not counted ---------------------------------------------

  it('groups the uncounted lines as the server grouped them, with each reason in words', async () => {
    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(UNRESOLVED_GROUP_HEADINGS.unmapped);
    expect(text(element)).toContain('1 cup oat milk');
    expect(text(element)).toContain(LINE_REASON_TEXT.NoMapping!);
    expect(text(element)).toContain('1 of 2 required ingredients counted');
  });

  it('keeps optional and unquantified lines visible, left out on purpose', async () => {
    service.estimateOutcome = {
      status: 'estimated',
      estimate: estimate({
        lines: [
          line(),
          UNMAPPED_LINE,
          line({ lineId: 'l3', displayText: 'flaky salt, to taste', outcome: 'Optional', required: false }),
          line({
            lineId: 'l4',
            displayText: 'a splash of oil',
            outcome: 'NotQuantified',
            reason: 'NoUnit',
            required: false,
          }),
        ],
      }),
    };

    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(PANEL_COPY.otherLinesHeading);
    expect(text(element)).toContain('flaky salt, to taste');
    expect(text(element)).toContain(OUTCOME_LABELS.Optional);
    expect(text(element)).toContain('a splash of oil');
    expect(text(element)).toContain(LINE_REASON_TEXT.NoUnit!);
  });

  it('shows what was counted on request, with its grams, basis and source record', async () => {
    const element = await render();
    await estimated(element);

    await click(element, 'what was counted');

    expect(text(element)).toContain('250 g');
    expect(text(element)).toContain(BASIS_LABELS.Mass);
    expect(text(element)).toContain('fdc · 173410 · version 2026-01-01');
  });

  it('actually hides a collapsed section, not just its aria-expanded', async () => {
    const element = await render();
    await estimated(element);

    const shut = buttonWith(element, 'what was counted')!;
    const list = element.querySelector(`#${shut.getAttribute('aria-controls')}`) as HTMLElement;
    expect(getComputedStyle(list).display).toBe('none');

    shut.click();
    await settle();
    expect(getComputedStyle(list).display).not.toBe('none');
  });

  it('announces what was worked out, counting what is not counted', async () => {
    const element = await render();
    await estimated(element);

    const live = element.querySelector('[role="status"].cp-sr-only');
    expect(live?.textContent).toContain('Estimated 2 nutrients against version 4.');
    expect(live?.textContent).toContain('1 required ingredient is not counted.');
  });

  // ---- Mapping review ---------------------------------------------------

  it('reads the mapping for the line’s platform ingredient, joined from the saved recipe', async () => {
    const element = await render();
    await openMapping(element);

    expect(service.mappingCalls).toEqual(['i2']);
    expect(text(element)).toContain('Oat milk, unsweetened');
    expect(text(element)).toContain('fdc · 174832 · version 2026-02-01');
  });

  it('shows a low confidence as a low confidence', async () => {
    const element = await render();
    await openMapping(element);

    expect(text(element)).toContain(MATCH_CONFIDENCE_LABELS.Low);
    expect(text(element)).toContain(MATCH_METHOD_LABELS.AutomaticSearch);
    expect(text(element)).toContain(rejectedReferencesSentence(2));
    // The portion, and whose figure it is: one of the two origins is this workspace's own override.
    expect(text(element)).toContain(
      portionText({ measurementUnitId: 'u1', amount: 1, grams: 240, label: 'cup', origin: 'Reference' }),
    );
  });

  it('says there is nothing to review for a line with no platform ingredient', async () => {
    service.estimateOutcome = {
      status: 'estimated',
      estimate: estimate({
        lines: [line(), line({ ...UNMAPPED_LINE, lineId: 'l9', reason: 'NoIngredient' })],
        unresolved: { unmapped: ['l9'], needsReview: [], unconvertible: [], notQuantified: [] },
      }),
    };

    const element = await render();
    await estimated(element);

    expect(text(element)).toContain(LINE_REASON_TEXT.NoIngredient!);

    // Scoped to that line: another line on the same estimate does have an ingredient, and does offer the
    // review.
    const row = Array.from(element.querySelectorAll('li.line')).find((candidate) =>
      candidate.textContent?.includes('1 cup oat milk'),
    ) as HTMLElement;
    expect(row.textContent).toContain(PANEL_COPY.mappingNoIngredient);
    expect(Array.from(row.querySelectorAll('button'))).toEqual([]);
  });

  it('says plainly when nothing has been proposed, and offers no dead control', async () => {
    service.mappingOutcome = { status: 'not_found' };

    const element = await render();
    await openMapping(element);

    expect(text(element)).toContain(PANEL_COPY.mappingNone);
    expect(buttonWith(element, PANEL_COPY.confirm)).toBeUndefined();
  });

  it('confirms with the token of the mapping as read, and marks the figures out of date', async () => {
    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.confirm);

    expect(service.decisionCalls).toEqual([{ operation: 'confirm', ingredientId: 'i2', token: 'token-1' }]);
    expect(text(element)).toContain(PANEL_COPY.confirmed);
    // The figures are not rewritten under the creator; they say they predate the decision.
    expect(text(element)).toContain(PANEL_COPY.staleMappingPill);
    expect(text(element)).toContain(PANEL_COPY.staleMapping);
    expect(text(element)).toContain('1235 kcal');
    expect(service.estimateCalls.length).toBe(1);
  });

  it('offers to confirm the newer version when the mapping is under review', async () => {
    service.mappingOutcome = {
      status: 'found',
      mapping: mapping({ status: 'NeedsReview', reviewSourceVersion: '2026-05-01' }),
    };

    const element = await render();
    await openMapping(element);

    expect(text(element)).toContain('2026-05-01');
    expect(buttonWith(element, PANEL_COPY.confirmNewVersion)).toBeDefined();
  });

  it('asks before turning a reference down, because this screen cannot take it back', async () => {
    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.reject);

    expect(confirmService.requests.length).toBe(1);
    expect(confirmService.requests[0].tone).toBe('danger');
    expect(confirmService.requests[0].message).toContain('cannot propose another one');
    expect(service.decisionCalls[0].operation).toBe('reject');
  });

  it('turns nothing down when the question is declined', async () => {
    confirmService.answer = false;

    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.reject);

    expect(service.decisionCalls.length).toBe(0);
  });

  it('reads the mapping again after a conflict, and does not resend the decision', async () => {
    service.decisionOutcome = { status: 'conflict' };

    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.confirm);

    expect(text(element)).toContain(PANEL_COPY.conflict);
    expect(service.decisionCalls.length).toBe(1);
    expect(service.mappingCalls).toEqual(['i2', 'i2']);
  });

  it('says there is nothing left to decide when the mapping has already moved', async () => {
    service.decisionOutcome = { status: 'state_conflict' };

    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.confirm);

    expect(text(element)).toContain(PANEL_COPY.stateConflict);
  });

  it('names the role a decision takes rather than offering it to a Contributor', async () => {
    memberships.setRole('Contributor');

    const element = await render();
    await openMapping(element);

    expect(text(element)).toContain(PANEL_COPY.mappingNeedsEditor);
    expect(buttonWith(element, PANEL_COPY.confirm)).toBeUndefined();
    expect(buttonWith(element, PANEL_COPY.reject)).toBeUndefined();
  });

  it('reports a refused decision as a role refusal, not as a lost session', async () => {
    service.decisionOutcome = { status: 'forbidden' };

    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.confirm);

    expect(text(element)).toContain(PANEL_COPY.forbidden);
  });

  it('says a failed save wrote nothing, in the server’s words', async () => {
    service.decisionOutcome = { status: 'retryable', message: 'The store refused the write. Nothing was saved.' };

    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.confirm);

    expect(text(element)).toContain('Nothing was saved');
    // Still offered: both 503s are safe to send again.
    expect(buttonWith(element, PANEL_COPY.confirm)).toBeDefined();
  });

  it('shows an unresolved mapping’s reason and offers no decision', async () => {
    service.mappingOutcome = {
      status: 'found',
      mapping: mapping({
        status: 'Unresolved',
        reference: null,
        portion: null,
        matchMethod: null,
        confidence: null,
        unresolvedReason: 'CreatorDeclined',
      }),
    };

    const element = await render();
    await openMapping(element);

    expect(text(element)).toContain(UNRESOLVED_REASON_TEXT.CreatorDeclined);
    expect(text(element)).toContain(PANEL_COPY.mappingNothingToDecide);
    expect(buttonWith(element, PANEL_COPY.confirm)).toBeUndefined();
  });

  it('offers the same mapping review on a counted line, where a low confidence is otherwise invisible', async () => {
    // An estimate line carries no match confidence, so a counted figure resting on a Low-confidence
    // reference can only be seen by opening its mapping. Leaving the control off counted rows would hide it.
    service.mappingOutcome = {
      status: 'found',
      mapping: mapping({
        ingredientId: 'i1',
        status: 'Confirmed',
        confidence: 'Low',
        confirmedAt: '2026-10-01T00:00:00Z',
      }),
    };

    const element = await render();
    await estimated(element);
    await click(element, 'what was counted');

    const counted = element.querySelector('[id$="-counted"]') as HTMLElement;
    const review = Array.from(counted.querySelectorAll('button')).find((button) =>
      button.textContent?.includes(PANEL_COPY.reviewMapping),
    );
    expect(review).withContext('a counted line offers its mapping too').toBeDefined();

    review!.click();
    await settle();

    expect(service.mappingCalls).toContain('i1');
    expect(text(element)).toContain(MATCH_CONFIDENCE_LABELS.Low);
    expect(text(element)).toContain(PANEL_COPY.mappingNothingToDecide);
  });

  it('names the line each review control acts on, for a screen reader', async () => {
    const element = await render();
    await estimated(element);

    const review = buttonWith(element, PANEL_COPY.reviewMapping)!;
    expect(review.querySelector('.cp-sr-only')?.textContent).toContain('1 cup oat milk');
  });

  it('takes focus to the mapping after a decision, rather than losing it with the button', async () => {
    const element = await render();
    await openMapping(element);
    await click(element, PANEL_COPY.confirm);

    // Confirming leaves nothing to decide, so the focused button is removed; the region takes focus so the
    // new state is what gets read, and focus does not fall to the body inside an open drawer.
    const region = document.getElementById(
      fixture.componentInstance.mappingElementId('l2'),
    );
    expect(document.activeElement).toBe(region);
  });

  it('lets a keyboard reach the figures when the table has to scroll', async () => {
    const element = await render();
    await estimated(element);

    const scroll = element.querySelector('.table-scroll');
    expect(scroll?.getAttribute('tabindex')).toBe('0');
    expect(scroll?.getAttribute('aria-labelledby')).toBe(fixture.componentInstance.estimateHeadingId);
  });

  it('says a missing per-serving figure rather than leaving a dash a reader cannot hear', async () => {
    service.estimateOutcome = {
      status: 'estimated',
      estimate: estimate({
        nutrients: [
          {
            code: 'Energy',
            unit: 'Kilocalorie',
            wholeRecipe: amount(),
            perServing: null,
            complete: true,
            linesMissingIt: [],
          },
        ],
      }),
    };

    const element = await render();
    await estimated(element);

    expect(element.querySelector('.figure .cp-sr-only')?.textContent).toContain(PANEL_COPY.notStated);
  });

  it('ties an incomplete nutrient’s note to the row it is about', async () => {
    const element = await render();
    await estimated(element);

    const header = Array.from(element.querySelectorAll('th[scope="row"]')).find((cell) =>
      cell.textContent?.includes(NUTRIENT_LABELS.VitaminD),
    );
    const noteId = header?.getAttribute('aria-describedby');
    expect(noteId).toBeTruthy();
    expect(element.querySelector(`#${noteId}`)?.textContent).toContain(incompleteNutrientSentence(1));
  });

  // ---- Refusals ---------------------------------------------------------

  it('puts a refused serving count on the field it is about', async () => {
    service.estimateOutcome = {
      status: 'invalid_request',
      fieldErrors: { servings: ['Enter a number above 0.'] },
    };

    const element = await render();
    await estimated(element);

    expect(element.querySelector('cp-field')?.textContent).toContain('Enter a number above 0.');
  });

  it('keeps the server’s sentence for a source that cannot be asked, and shows no estimate', async () => {
    service.estimateOutcome = {
      status: 'source_unavailable',
      message: 'The nutrition source is not available on this installation.',
    };

    const element = await render();
    await estimated(element);

    expect(text(element)).toContain('not available on this installation');
    expect(element.querySelector('.nutrients')).toBeNull();
  });

  it('answers each refusal in words a creator can act on', async () => {
    const cases: readonly [NutritionEstimateOutcome, string][] = [
      [
        { status: 'quantities_unprocessable', message: 'The total energy is too large to state.' },
        'too large to state',
      ],
      [{ status: 'version_not_found' }, PANEL_COPY.versionNotFound],
      [{ status: 'not_found' }, PANEL_COPY.notFound],
      [{ status: 'unauthorized' }, PANEL_COPY.unauthorized],
      [{ status: 'unavailable' }, PANEL_COPY.unavailable],
    ];

    for (const [outcome, message] of cases) {
      service.estimateOutcome = outcome;
      const element = await render();
      await estimated(element);

      expect(text(element)).toContain(message);
      expect(element.querySelector('[role="alert"]')).not.toBeNull();
    }
  });

  it('keeps an estimate on screen when the next request is refused', async () => {
    const element = await render();
    await estimated(element);

    service.estimateOutcome = { status: 'unavailable' };
    await click(element, PANEL_COPY.recheck);

    expect(text(element)).toContain(PANEL_COPY.unavailable);
    expect(text(element)).toContain('1235 kcal');
    expect(text(element)).toContain('Read against version 4');
  });

  it('says the figures are out of date when the recipe has moved past the version they came from', async () => {
    const element = await render();
    await estimated(element);
    expect(text(element)).not.toContain(PANEL_COPY.stale);

    fixture.componentRef.setInput('sourceVersionNumber', 5);
    await settle();

    expect(text(element)).toContain(PANEL_COPY.stalePill);
    expect(text(element)).toContain(PANEL_COPY.stale);
    expect(text(element)).toContain('1235 kcal');
  });

  // ---- The claims this panel may not make -------------------------------

  it('never calls an estimate a label value, a certified figure or a guarantee', () => {
    const forbidden = [
      /\bcertified?\b/i,
      /\bguarantee[ds]?\b/i,
      /nutrition facts/i,
      /daily value/i,
      /%\s*dv/i,
      /\bverified\b/i,
      // Deliberately not /\bexact(ly)?\b/: this panel shows the server's unrounded figures under "Exact
      // values", and says a mapping's name "matched exactly" — both are true statements, one about a number
      // the server really did compute exactly and one about a string comparison. The claims that matter here
      // are the ones above: a certification, a guarantee, a label value or a verification.
    ];

    const ownCopy: string[] = [
      ...Object.values(PANEL_COPY),
      ...Object.values(NUTRIENT_LABELS),
      ...Object.values(OUTCOME_LABELS),
      ...Object.values(BASIS_LABELS),
      ...Object.values(UNRESOLVED_REASON_TEXT),
      ...Object.values(UNRESOLVED_GROUP_HEADINGS),
      ...Object.values(MATCH_METHOD_LABELS),
      ...Object.values(MATCH_CONFIDENCE_LABELS),
      ...Object.values(PORTION_ORIGIN_TEXT),
      rejectedReferencesSentence(1),
      rejectedReferencesSentence(2),
      ...Object.values(LINE_REASON_TEXT).filter((value): value is string => value !== null),
      PUBLISHABLE_SENTENCE,
      publishBlockedSentence(1),
      publishBlockedSentence(3),
      incompleteNutrientSentence(1),
      incompleteNutrientSentence(2),
      confidenceSentence('Mass'),
      confidenceSentence('ReferencePortion'),
      confidenceSentence('CreatorPortion'),
    ];

    for (const sentence of ownCopy) {
      for (const pattern of forbidden) {
        expect(sentence).not.toMatch(pattern);
      }
    }
  });

  it('formats a figure without changing it', () => {
    // `toFixed` is called with the precision the server already rounded to, so it pads and never rounds.
    expect(amountText(amount({ displayLow: 1235, displayPrecision: 0 }))).toBe('1235');
    expect(amountText(amount({ displayLow: 1.5, displayPrecision: 1 }))).toBe('1.5');
    expect(amountText(amount({ displayLow: 2, displayPrecision: 1 }))).toBe('2.0');
    expect(amountText(amount({ trace: true }))).toBe(PANEL_COPY.traceText);
    expect(
      amountText(amount({ displayLow: 10, displayHigh: 12, displayPrecision: 1, isRange: true })),
    ).toBe('10.0 to 12.0');
  });
});
