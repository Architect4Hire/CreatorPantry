import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  AllergenAnalysis,
  DietaryAllergenAnalysis,
  DietaryAnalysis,
} from '../../models/dietary-allergen-analysis.models';
import { DescribedReferenceEntry } from '../../models/reference.models';
import {
  DietaryAllergenAnalysisOutcome,
  DietaryAllergenAnalysisService,
} from '../../services/dietary-allergen-analysis.service';
import { ListDescribedReferenceEntriesOutcome, ReferenceService } from '../../services/reference.service';
import {
  ALLERGEN_FINDING_LABELS,
  GROUP_HEADINGS,
  ALLERGEN_FINDING_TONES,
  COMPATIBILITY_WORDS,
  CONFIDENCE_CAVEAT,
  CONFIDENCE_LABELS,
  CONFIDENCE_TONES,
  DIETARY_FINDING_LABELS,
  DIETARY_FINDING_TONES,
  PANEL_COPY,
  PRESENCE_WORDS,
  REASON_TEXT,
  RecipeDietaryAllergenComponent,
  SOURCE_KIND_LABELS,
  conflictSentence,
  NOT_COUNTED_REASONS,
} from './recipe-dietary-allergen.component';

const DISCLAIMER =
  'This analysis reads reviewed ingredient records only. It cannot account for shared equipment, facilities, ' +
  'or how a particular product was made, and it is not a guarantee that a dish is free of any allergen or ' +
  'suitable for any person. Check every label, and ask anyone you are cooking for.';

function entry(id: string, displayName: string, description: string): DescribedReferenceEntry {
  return { id, code: id, displayName, description };
}

const ALLERGENS: readonly DescribedReferenceEntry[] = [
  entry('a-milk', 'Milk', 'Milk and milk-derived ingredients, including butter, cream and whey.'),
  entry('a-egg', 'Egg', 'Eggs from birds and egg-derived ingredients such as albumen and mayonnaise.'),
];

const PROFILES: readonly DescribedReferenceEntry[] = [
  entry('d-vegan', 'Vegan', 'Excludes all animal-derived ingredients.'),
];

function allergen(overrides: Partial<AllergenAnalysis> = {}): AllergenAnalysis {
  return {
    allergenId: 'a-milk',
    allergenName: 'Milk',
    finding: 'Contains',
    confidence: 'Partial',
    coverage: { linesRead: 3, linesWithCountedRecords: 2 },
    statement: 'Contains Milk, according to the cited records. One other ingredient has no reviewed record.',
    containingLines: ['l1'],
    possibleSources: ['l2'],
    unknownLines: ['l3'],
    conflictingLines: [],
    lines: [
      {
        lineId: 'l1',
        displayText: '250 g butter',
        isOptional: false,
        ingredientId: 'i1',
        finding: 'Contains',
        reason: 'None',
        conflict: false,
        evidence: [
          {
            presence: 'Present',
            sourceCode: 'fdc',
            sourceName: 'FoodData Central',
            sourceKind: 'OfficialDatabase',
            citation: 'FDC 173410',
            effectiveFrom: '2026-01-01',
            evidenceNote: 'Butter is a milk product.',
            counted: true,
            countedReason: 'Counted',
          },
        ],
      },
      {
        lineId: 'l2',
        displayText: 'a knob of margarine',
        isOptional: true,
        ingredientId: 'i2',
        finding: 'Possible',
        reason: 'None',
        conflict: false,
        evidence: [
          {
            presence: 'PossiblePresence',
            sourceCode: 'maker',
            sourceName: 'Maker label',
            sourceKind: 'Manufacturer',
            citation: 'label 2026',
            effectiveFrom: '2026-02-02',
            evidenceNote: 'May contain milk; made on shared equipment.',
            counted: true,
            countedReason: 'Counted',
          },
        ],
      },
      {
        lineId: 'l3',
        displayText: 'a pinch of nutmeg',
        isOptional: false,
        ingredientId: null,
        finding: 'Unknown',
        reason: 'NotARecognisedIngredient',
        conflict: false,
        evidence: [],
      },
    ],
    ...overrides,
  };
}

function dietary(overrides: Partial<DietaryAnalysis> = {}): DietaryAnalysis {
  return {
    dietaryProfileId: 'd-vegan',
    dietaryProfileName: 'Vegan',
    finding: 'DoesNotFit',
    confidence: 'Disputed',
    coverage: { linesRead: 1, linesWithCountedRecords: 1 },
    statement: 'Does not fit Vegan, according to the cited records.',
    incompatibleLines: ['l1'],
    dependsOnProductLines: [],
    unknownLines: [],
    conflictingLines: ['l1'],
    lines: [
      {
        lineId: 'l1',
        displayText: '250 g butter',
        isOptional: false,
        ingredientId: 'i1',
        finding: 'Incompatible',
        reason: 'None',
        conflict: true,
        evidence: [
          {
            compatibility: 'Compatible',
            sourceCode: 'forum',
            sourceName: 'Community notes',
            sourceKind: 'CommunityContributed',
            citation: 'thread 91',
            effectiveFrom: '2025-06-01',
            evidenceNote: 'Someone reported a plant-based version.',
            counted: false,
            countedReason: 'UnvettedKind',
          },
        ],
      },
    ],
    ...overrides,
  };
}

function analysis(overrides: Partial<DietaryAllergenAnalysis> = {}): DietaryAllergenAnalysis {
  return {
    recipeId: 'r1',
    recipeVersionId: 'v4',
    versionNumber: 4,
    methodVersion: 'dietary-allergen/1',
    evaluatedOn: '2026-10-20',
    crossContactDisclaimer: DISCLAIMER,
    allergens: [allergen()],
    dietary: [dietary()],
    sources: [
      {
        code: 'fdc',
        name: 'FoodData Central',
        kind: 'OfficialDatabase',
        citation: 'USDA FoodData Central',
        licenseCode: 'CC0-1.0',
        licenseUrl: 'https://example.test/cc0',
        isActive: true,
        latestEffectiveFrom: '2026-01-01',
      },
      {
        code: 'forum',
        name: 'Community notes',
        kind: 'CommunityContributed',
        citation: 'thread 91',
        licenseCode: null,
        licenseUrl: null,
        isActive: false,
        latestEffectiveFrom: '2025-06-01',
      },
    ],
    ...overrides,
  };
}

interface AnalyseCall {
  readonly workspaceSlug: string;
  readonly recipeId: string;
  readonly versionNumber: number;
  readonly allergenIds: readonly string[];
  readonly dietaryProfileIds: readonly string[];
}

class StubAnalysisService {
  outcome: DietaryAllergenAnalysisOutcome = { status: 'analysed', analysis: analysis() };
  calls: AnalyseCall[] = [];

  /** Holds the next answer, so a test can look at the panel while the request is in flight. */
  holdNext = false;
  private release: (() => void) | null = null;

  analyse(
    workspaceSlug: string,
    recipeId: string,
    versionNumber: number,
    selection: { allergenIds: readonly string[]; dietaryProfileIds: readonly string[] },
  ): Promise<DietaryAllergenAnalysisOutcome> {
    this.calls.push({ workspaceSlug, recipeId, versionNumber, ...selection });

    if (!this.holdNext) return Promise.resolve(this.outcome);

    this.holdNext = false;
    return new Promise<DietaryAllergenAnalysisOutcome>((resolve) => {
      this.release = () => resolve(this.outcome);
    });
  }

  finish(): void {
    this.release?.();
    this.release = null;
  }
}

class StubReferenceService {
  allergens: ListDescribedReferenceEntriesOutcome = { status: 'found', entries: ALLERGENS };
  profiles: ListDescribedReferenceEntriesOutcome = { status: 'found', entries: PROFILES };
  allergenCalls = 0;
  profileCalls = 0;

  listAllergens(): Promise<ListDescribedReferenceEntriesOutcome> {
    this.allergenCalls += 1;
    return Promise.resolve(this.allergens);
  }

  listDietaryProfiles(): Promise<ListDescribedReferenceEntriesOutcome> {
    this.profileCalls += 1;
    return Promise.resolve(this.profiles);
  }
}

describe('RecipeDietaryAllergenComponent', () => {
  let service: StubAnalysisService;
  let reference: StubReferenceService;
  let fixture: ComponentFixture<RecipeDietaryAllergenComponent>;

  async function render(versionNumber: number | null = 4, dirty = false): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeDietaryAllergenComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('sourceVersionNumber', versionNumber);
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

  /** Picks an option in one of the two choice groups by its visible label. */
  async function pick(element: HTMLElement, label: string): Promise<void> {
    const tile = Array.from(element.querySelectorAll('label.tile')).find((candidate) =>
      candidate.querySelector('.label')?.textContent?.trim() === label,
    );
    (tile?.querySelector('input') as HTMLInputElement | null)?.click();
    await settle();
  }

  async function analyseWith(element: HTMLElement, label = 'Milk'): Promise<void> {
    await pick(element, label);
    await click(element, PANEL_COPY.submit);
  }

  beforeEach(() => {
    service = new StubAnalysisService();
    reference = new StubReferenceService();

    TestBed.configureTestingModule({
      imports: [RecipeDietaryAllergenComponent],
      providers: [
        { provide: DietaryAllergenAnalysisService, useValue: service },
        { provide: ReferenceService, useValue: reference },
      ],
    });
  });

  // ---- The selection ----------------------------------------------------

  it('offers each allergen and dietary pattern with the description that says what it covers', async () => {
    const element = await render();

    expect(reference.allergenCalls).toBe(1);
    expect(reference.profileCalls).toBe(1);

    const labels = Array.from(element.querySelectorAll('label.tile .label')).map((node) =>
      node.textContent?.trim(),
    );
    expect(labels).toEqual(['Milk', 'Egg', 'Vegan']);

    // The name without the description is half the fact, so the hint is never dropped.
    const hints = Array.from(element.querySelectorAll('label.tile .hint')).map((node) => node.textContent?.trim());
    expect(hints).toContain(ALLERGENS[0].description);
    expect(hints).toContain(PROFILES[0].description);
  });

  it('caps each group at the number the server accepts, so the limit shows before it bites', async () => {
    const element = await render();

    const groups = Array.from(element.querySelectorAll('cp-choice-group'));
    expect(groups.length).toBe(2);
    for (const group of groups) {
      expect(group.querySelector('[role="group"]')).not.toBeNull();
    }
    expect(fixture.componentInstance.maxSubjects).toBe(20);
  });

  it('will not ask until something is picked, and says why', async () => {
    const element = await render();

    expect(buttonWith(element, PANEL_COPY.submit)?.disabled).toBeTrue();
    expect(text(element)).toContain(PANEL_COPY.pickSomething);

    await pick(element, 'Milk');

    expect(buttonWith(element, PANEL_COPY.submit)?.disabled).toBeFalse();
    expect(text(element)).not.toContain(PANEL_COPY.pickSomething);
  });

  it('will not ask about a recipe with no saved version, and says that instead', async () => {
    const element = await render(null);

    expect(text(element)).toContain(PANEL_COPY.noVersion);
    await pick(element, 'Milk');
    expect(buttonWith(element, PANEL_COPY.submit)?.disabled).toBeTrue();
    expect(service.calls.length).toBe(0);
  });

  it('sends the picked ids against the saved version it was given', async () => {
    const element = await render();
    await pick(element, 'Milk');
    await pick(element, 'Vegan');
    await click(element, PANEL_COPY.submit);

    expect(service.calls).toEqual([
      {
        workspaceSlug: 'cozy-fall',
        recipeId: 'r1',
        versionNumber: 4,
        allergenIds: ['a-milk'],
        dietaryProfileIds: ['d-vegan'],
      },
    ]);
  });

  it('says that unsaved edits are not in what it read', async () => {
    const element = await render(4, true);

    expect(text(element)).toContain(PANEL_COPY.dirtyPill);
    expect(text(element)).toContain(PANEL_COPY.dirty);
  });

  it('reports both lists failing as nothing to pick, and reads them again on request', async () => {
    reference.allergens = { status: 'unavailable' };
    const element = await render();

    expect(text(element)).toContain(PANEL_COPY.listsUnavailable);
    expect(element.querySelector('cp-choice-group')).toBeNull();

    reference.allergens = { status: 'found', entries: ALLERGENS };
    await click(element, 'Try again');

    expect(reference.allergenCalls).toBe(2);
    expect(element.querySelectorAll('label.tile').length).toBe(3);
  });

  // ---- The answer -------------------------------------------------------

  it("renders the server's statement verbatim, and writes no sentence of its own about a finding", async () => {
    const element = await render();
    await analyseWith(element);

    const statements = Array.from(element.querySelectorAll('.statement')).map((node) => node.textContent?.trim());
    expect(statements).toEqual([allergen().statement, dietary().statement]);
  });

  it('names each finding in words beside its tone, and names the kind it is about', async () => {
    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(ALLERGEN_FINDING_LABELS.Contains);
    expect(text(element)).toContain(DIETARY_FINDING_LABELS.DoesNotFit);
    expect(text(element)).toContain('Allergen');
    expect(text(element)).toContain('Dietary pattern');
  });

  it('shows the cross-contact disclaimer on every answer, as the server wrote it', async () => {
    const element = await render();
    await analyseWith(element);

    // The body span, not the host: the host also holds the decorative glyph, which is aria-hidden.
    const disclaimer = element.querySelector('cp-notice.disclaimer .body');
    expect(disclaimer?.textContent?.trim()).toBe(DISCLAIMER);
  });

  it('shows the disclaimer on the best answer the records can give too', async () => {
    service.outcome = {
      status: 'analysed',
      analysis: analysis({
        allergens: [
          allergen({
            finding: 'NotListedInRecords',
            confidence: 'Recorded',
            statement:
              'Milk is not listed in the cited records for any ingredient. ' +
              'This is not a statement that the recipe is free of Milk.',
            containingLines: [],
            possibleSources: [],
            unknownLines: [],
          }),
        ],
        dietary: [],
      }),
    };

    const element = await render();
    await analyseWith(element);

    expect(element.querySelector('cp-notice.disclaimer .body')?.textContent?.trim()).toBe(DISCLAIMER);
    expect(text(element)).toContain(ALLERGEN_FINDING_LABELS.NotListedInRecords);
    expect(text(element)).toContain('not a statement that the recipe is free of Milk');
  });

  it('never paints a finding as a success, whichever way it came out', async () => {
    service.outcome = {
      status: 'analysed',
      analysis: analysis({
        allergens: [allergen({ finding: 'NotListedInRecords', confidence: 'Recorded' })],
        dietary: [dietary({ finding: 'FitsAsRecorded', confidence: 'Recorded', conflictingLines: [] })],
      }),
    };

    const element = await render();
    await analyseWith(element);

    // A tick beside "not listed" is how a reading of records becomes read as a clearance.
    expect(element.querySelectorAll('cp-status-pill[data-tone="success"]').length).toBe(0);
  });

  it('says how complete the records are, and that this is not how likely the finding is', async () => {
    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(CONFIDENCE_LABELS.Partial);
    expect(text(element)).toContain('2 of 3 lines read have a record that counts.');
    expect(text(element)).toContain(CONFIDENCE_CAVEAT);
  });

  /// <summary>Reversed by 14.7's L-1: the caveat is now shown on every finding.</summary>
  it('keeps the caveat on a finding whose records are complete and agree', async () => {
    service.outcome = {
      status: 'analysed',
      analysis: analysis({
        allergens: [allergen({ confidence: 'Recorded', unknownLines: [] })],
        dietary: [],
      }),
    };

    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(CONFIDENCE_LABELS.Recorded);
    // The caveat used to be hidden here, which is exactly where the answer reads most like reassurance: a
    // complete, undisputed set of records behind "not listed in records" (14.7, L-1).
    expect(text(element)).toContain(CONFIDENCE_CAVEAT);
  });

  it('groups the lines a source speaks about, and says where the hidden-source group came from', async () => {
    const element = await render();
    await analyseWith(element);

    const headings = Array.from(element.querySelectorAll('.group-title')).map((node) =>
      node.textContent?.replace(/\s+/g, ' ').trim(),
    );
    expect(headings.some((heading) => heading?.startsWith(GROUP_HEADINGS.contains))).toBeTrue();
    expect(headings.some((heading) => heading?.startsWith(GROUP_HEADINGS.possible))).toBeTrue();
    expect(headings.some((heading) => heading?.startsWith(GROUP_HEADINGS.unknown))).toBeTrue();

    expect(text(element)).toContain(PANEL_COPY.hiddenSources);
  });

  it("keeps the creator's own words for a line, and marks an optional one as read anyway", async () => {
    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain('250 g butter');
    expect(text(element)).toContain('a knob of margarine');
    expect(text(element)).toContain('Optional');
  });

  it('says why a line has no finding to point at', async () => {
    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(REASON_TEXT.NotARecognisedIngredient!);
  });

  it('flags a disagreement, says the more cautious word was kept, and cites both', async () => {
    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(conflictSentence(1));
    expect(text(element)).toContain(PANEL_COPY.lineConflict);
  });

  it('shows where a finding came from on request, with the source and the date it took effect', async () => {
    const element = await render();
    await analyseWith(element);

    const disclosure = buttonWith(element, 'Where this comes from');
    expect(disclosure?.getAttribute('aria-expanded')).toBe('false');
    const list = element.querySelector(`#${disclosure?.getAttribute('aria-controls')}`);
    expect(list?.hasAttribute('hidden')).toBeTrue();

    await click(element, 'Where this comes from');

    expect(buttonWith(element, 'Hide where this comes from')?.getAttribute('aria-expanded')).toBe('true');
    expect(text(element)).toContain(PRESENCE_WORDS.Present);
    expect(text(element)).toContain('FoodData Central');
    expect(text(element)).toContain(SOURCE_KIND_LABELS.OfficialDatabase);
    expect(text(element)).toContain('Butter is a milk product.');
  });

  it('actually hides a group it says is collapsed, not just its aria-expanded', async () => {
    const element = await render();
    await analyseWith(element);

    // The regression this pins: every list in here is `display: grid`, which beats the UA rule behind the
    // `hidden` attribute. A group reading "Show" while its lines are on screen would state records the
    // creator had collapsed out of the way.
    const shut = buttonWith(element, 'Show');
    expect(shut).withContext('a group shut by default').toBeDefined();

    const list = element.querySelector(`#${shut?.getAttribute('aria-controls')}`) as HTMLElement;
    expect(getComputedStyle(list).display).toBe('none');

    shut?.click();
    await settle();

    expect(getComputedStyle(list).display).not.toBe('none');
  });

  it('hides a line\u2019s evidence until it is asked for, in fact as well as in its label', async () => {
    const element = await render();
    await analyseWith(element);

    const disclosure = buttonWith(element, 'Where this comes from')!;
    const list = element.querySelector(`#${disclosure.getAttribute('aria-controls')}`) as HTMLElement;
    expect(getComputedStyle(list).display).toBe('none');

    disclosure.click();
    await settle();

    expect(getComputedStyle(list).display).not.toBe('none');
  });

  it('says a request is in flight, and keeps the button focusable while it is', async () => {
    const element = await render();
    await pick(element, 'Milk');

    const submit = buttonWith(element, PANEL_COPY.submit)!;
    submit.focus();
    service.holdNext = true;
    submit.click();
    await settle();

    const working = buttonWith(element, PANEL_COPY.working)!;
    // Not `disabled`: a focused button that becomes disabled drops focus to the body, and this one is inside
    // an open drawer. The handler is what refuses a second request.
    expect(working.disabled).toBeFalse();
    expect(working.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(working);
    expect(element.querySelector('[role="status"].cp-sr-only')?.textContent).toContain(PANEL_COPY.checking);

    working.click();
    await settle();
    expect(service.calls.length).toBe(1);

    service.finish();
    await settle();

    expect(text(element)).toContain(allergen().statement);
  });

  it('cites a word that does not count, and says why it does not move the finding', async () => {
    const element = await render();
    await analyseWith(element, 'Vegan');
    await click(element, 'Where this comes from');

    expect(text(element)).toContain(COMPATIBILITY_WORDS.Compatible);
    expect(text(element)).toContain('Cited, not counted');
    // The server says which rule refused the word; this panel maps that reason to a sentence (14.7, L-2).
    expect(text(element)).toContain(NOT_COUNTED_REASONS.UnvettedKind!);
  });

  it('lists every source read, counted or not, with its licence and the date of its latest record', async () => {
    const element = await render();
    await analyseWith(element);

    const sources = element.querySelector('.sources');
    expect(sources?.textContent).toContain('FoodData Central');
    expect(sources?.textContent).toContain('USDA FoodData Central');
    expect(sources?.textContent).toContain('CC0-1.0');
    expect(sources?.textContent).toContain('Community notes');
    expect(sources?.textContent).toContain(PANEL_COPY.retiredSource);
    expect(sources?.querySelector('a')?.getAttribute('href')).toBe('https://example.test/cc0');
  });

  it('names the version, the date the records were read, and the method', async () => {
    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain('Read against version 4');
    expect(text(element)).toContain('20 October 2026');
    expect(text(element)).toContain('dietary-allergen/1');
  });

  it('says that empty records are empty records, not a clear recipe', async () => {
    service.outcome = {
      status: 'analysed',
      analysis: analysis({
        allergens: [allergen({ finding: 'Unknown', confidence: 'None', containingLines: [], possibleSources: [] })],
        dietary: [],
      }),
    };

    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(PANEL_COPY.nothingRecorded);
  });

  it('says there was nothing to read when the version has no ingredient lines', async () => {
    service.outcome = {
      status: 'analysed',
      analysis: analysis({
        allergens: [
          allergen({
            finding: 'Unknown',
            confidence: 'None',
            coverage: { linesRead: 0, linesWithCountedRecords: 0 },
            containingLines: [],
            possibleSources: [],
            unknownLines: [],
            lines: [],
          }),
        ],
        dietary: [],
      }),
    };

    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(PANEL_COPY.noLines);
  });

  it('announces what was read in one line, counting the findings and nothing more', async () => {
    const element = await render();
    await analyseWith(element);

    const summary = element.querySelector('[role="status"].cp-sr-only');
    expect(summary?.textContent).toContain('Read 1 allergen and 1 dietary pattern against version 4.');
    expect(summary?.textContent).toContain('1 contains');
    expect(summary?.textContent).toContain("1 doesn't fit");
  });

  it('says the answer is out of date when the recipe has moved past the version it read', async () => {
    const element = await render();
    await analyseWith(element);
    expect(text(element)).not.toContain(PANEL_COPY.stale);

    fixture.componentRef.setInput('sourceVersionNumber', 5);
    await settle();

    expect(text(element)).toContain(PANEL_COPY.stalePill);
    expect(text(element)).toContain(PANEL_COPY.stale);
    // Still shown: it was true of the version it names, and it names it.
    expect(text(element)).toContain(allergen().statement);
  });

  // ---- Refusals ---------------------------------------------------------

  it('puts a refused selection on the list it is about', async () => {
    service.outcome = {
      status: 'invalid_request',
      fieldErrors: { allergenIds: ['Choose no more than 20 allergens.'] },
    };

    const element = await render();
    await analyseWith(element);

    const problem = element.querySelector('cp-form-section p[role="alert"]');
    expect(problem?.textContent?.trim()).toContain('Choose no more than 20 allergens.');
  });

  it("surfaces a refusal about a field neither list is bound to, in the server's own words", async () => {
    service.outcome = { status: 'invalid_request', fieldErrors: { versionNumber: ['Pick a saved version.'] } };

    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain('Pick a saved version.');
  });

  it('offers to reload the lists when the catalogue has moved under the selection', async () => {
    service.outcome = { status: 'subject_unprocessable', message: 'That allergen is no longer offered.' };

    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain('That allergen is no longer offered.');

    await click(element, 'Reload lists');
    expect(reference.allergenCalls).toBe(2);
  });

  it('falls back to its own wording when a 422 carries no sentence', async () => {
    service.outcome = { status: 'subject_unprocessable', message: '' };

    const element = await render();
    await analyseWith(element);

    expect(text(element)).toContain(PANEL_COPY.subjectUnprocessable);
  });

  it('answers each refusal in words a creator can act on', async () => {
    const cases: readonly [DietaryAllergenAnalysisOutcome, string][] = [
      [{ status: 'version_not_found' }, PANEL_COPY.versionNotFound],
      [{ status: 'not_found' }, PANEL_COPY.notFound],
      [{ status: 'unauthorized' }, PANEL_COPY.unauthorized],
      [{ status: 'unavailable' }, PANEL_COPY.unavailable],
    ];

    for (const [outcome, message] of cases) {
      service.outcome = outcome;
      const element = await render();
      await analyseWith(element);

      expect(text(element)).toContain(message);
      expect(element.querySelector('[role="alert"]')).not.toBeNull();
    }
  });

  it('keeps an analysis on screen when the next request is refused', async () => {
    const element = await render();
    await analyseWith(element);

    service.outcome = { status: 'unavailable' };
    await click(element, PANEL_COPY.recheck);

    expect(text(element)).toContain(PANEL_COPY.unavailable);
    // The numbers were true of the version printed above them, and that version is still printed.
    expect(text(element)).toContain(allergen().statement);
    expect(text(element)).toContain('Read against version 4');
  });

  // ---- The claim this panel may never make ------------------------------

  it('says nothing anywhere in its own copy about anything being safe, free from, certified or suitable', () => {
    // The server holds this line for the sentences it composes (B-36). This is the same line for the sentences
    // written here — which is every sentence on screen except `statement` and `crossContactDisclaimer`.
    const forbidden = [
      /\bsafe\b/i,
      /\bunsafe\b/i,
      /free from/i,
      /free of/i,
      /\bcertified?\b/i,
      /\bsuitable\b/i,
      /\bclear\b/i,
      /\bcleared\b/i,
      /\bguarantee[ds]?\b/i,
      /no traces?\b/i,
    ];

    const ownCopy: string[] = [
      ...Object.values(PANEL_COPY),
      ...Object.values(ALLERGEN_FINDING_LABELS),
      ...Object.values(DIETARY_FINDING_LABELS),
      ...Object.values(CONFIDENCE_LABELS),
      ...Object.values(SOURCE_KIND_LABELS),
      ...Object.values(GROUP_HEADINGS),
      ...Object.values(PRESENCE_WORDS),
      ...Object.values(COMPATIBILITY_WORDS),
      ...Object.values(REASON_TEXT).filter((value): value is string => value !== null),
      CONFIDENCE_CAVEAT,
      conflictSentence(1),
      conflictSentence(2),
      ...Object.values(NOT_COUNTED_REASONS).filter((value): value is string => value !== null),
    ];

    for (const sentence of ownCopy) {
      for (const pattern of forbidden) {
        expect(sentence).not.toMatch(pattern);
      }
    }
  });

  it('gives no finding a success tone, in either vocabulary', () => {
    for (const tone of Object.values(ALLERGEN_FINDING_TONES)) expect(tone).not.toBe('success');
    for (const tone of Object.values(DIETARY_FINDING_TONES)) expect(tone).not.toBe('success');
    for (const tone of Object.values(CONFIDENCE_TONES)) expect(tone).not.toBe('success');
  });

  it('keeps the best answer and the unknown one at the same quiet tone', () => {
    // Neither is reassurance, and a quieter tone for one of them would rank them.
    expect(ALLERGEN_FINDING_TONES.NotListedInRecords).toBe(ALLERGEN_FINDING_TONES.Unknown);
    expect(DIETARY_FINDING_TONES.FitsAsRecorded).toBe(DIETARY_FINDING_TONES.Unknown);
  });
});
