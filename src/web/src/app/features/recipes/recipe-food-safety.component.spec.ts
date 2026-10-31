import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FoodSafetyWarning, FoodSafetyWarnings } from '../../models/food-safety.models';
import { FoodSafetyService, FoodSafetyWarningsOutcome } from '../../services/food-safety.service';
import {
  CATEGORY_LABELS,
  CONFIDENCE_LABELS,
  CONFIDENCE_TONES,
  EVIDENCE_STATUS_LABELS,
  FIELD_LABELS,
  PANEL_COPY,
  RecipeFoodSafetyComponent,
  TRIGGER_KIND_LABELS,
  foundSentence,
} from './recipe-food-safety.component';

const DISCLAIMER =
  'These checks look for a fixed set of high-risk methods and claims in the recipe’s own text and choices. ' +
  'Finding nothing is not a statement that this recipe is safe, and a warning is not a ruling that it is unsafe.';

const VERIFY_STATEMENT =
  'Verification required: no approved reference for this check exists on this installation. Check this ' +
  'against current, tested guidance.';

function warning(overrides: Partial<FoodSafetyWarning> = {}): FoodSafetyWarning {
  return {
    category: 'Canning',
    confidence: 'Detected',
    message: 'This recipe describes canning.',
    triggers: [{ field: 'Step', itemId: 's1', kind: 'Text', matched: 'Pressure-can at 10 lb for 75 minutes' }],
    evidence: {
      status: 'NoApprovedSource',
      citation: null,
      statement: VERIFY_STATEMENT,
      verificationRequired: true,
    },
    statedTemperatures: [],
    ...overrides,
  };
}

function warnings(overrides: Partial<FoodSafetyWarnings> = {}): FoodSafetyWarnings {
  return {
    recipeId: 'r1',
    recipeVersionId: 'v3',
    versionNumber: 3,
    methodVersion: 'food-safety-warnings/1',
    warnings: [warning()],
    unrecognisedTechniques: 0,
    disclaimer: DISCLAIMER,
    ...overrides,
  };
}

class StubFoodSafetyService {
  outcome: FoodSafetyWarningsOutcome = { status: 'found', warnings: warnings() };
  calls: { workspaceSlug: string; recipeId: string; versionNumber: number }[] = [];

  holdNext = false;
  private release: (() => void) | null = null;

  read(workspaceSlug: string, recipeId: string, versionNumber: number): Promise<FoodSafetyWarningsOutcome> {
    this.calls.push({ workspaceSlug, recipeId, versionNumber });

    if (!this.holdNext) return Promise.resolve(this.outcome);

    this.holdNext = false;
    return new Promise<FoodSafetyWarningsOutcome>((resolve) => {
      this.release = () => resolve(this.outcome);
    });
  }

  finish(): void {
    this.release?.();
    this.release = null;
  }
}

describe('RecipeFoodSafetyComponent', () => {
  let service: StubFoodSafetyService;
  let fixture: ComponentFixture<RecipeFoodSafetyComponent>;

  async function render(versionNumber: number | null = 3, dirty = false): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeFoodSafetyComponent);
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
    return Array.from(element.querySelectorAll('button')).find((button) => button.textContent?.trim().includes(label));
  }

  async function click(element: HTMLElement, label: string): Promise<void> {
    buttonWith(element, label)?.click();
    await settle();
  }

  async function checked(element: HTMLElement): Promise<void> {
    await click(element, PANEL_COPY.check);
  }

  beforeEach(() => {
    service = new StubFoodSafetyService();

    TestBed.configureTestingModule({
      imports: [RecipeFoodSafetyComponent],
      providers: [{ provide: FoodSafetyService, useValue: service }],
    });
  });

  it('says what the checks read, and asks before reading anything', async () => {
    const element = await render();

    expect(text(element)).toContain(PANEL_COPY.intro);
    expect(service.calls.length).toBe(0);
  });

  it('will not check a recipe with no saved version', async () => {
    const element = await render(null);

    expect(text(element)).toContain(PANEL_COPY.noVersion);
    expect(buttonWith(element, PANEL_COPY.check)?.disabled).toBeTrue();
  });

  it('reads the saved version it was given', async () => {
    const element = await render();
    await checked(element);

    expect(service.calls).toEqual([{ workspaceSlug: 'cozy-fall', recipeId: 'r1', versionNumber: 3 }]);
  });

  it("renders the server's sentences verbatim, and writes no verdict of its own", async () => {
    const element = await render();
    await checked(element);

    expect(element.querySelector('.message')?.textContent?.trim()).toBe(warning().message);
    expect(text(element)).toContain(VERIFY_STATEMENT);
    expect(element.querySelector('cp-notice.disclaimer .body')?.textContent?.trim()).toBe(DISCLAIMER);
  });

  it('names the category and how firmly the text shows it, in words', async () => {
    const element = await render();
    await checked(element);

    expect(text(element)).toContain(CATEGORY_LABELS.Canning);
    expect(text(element)).toContain(CONFIDENCE_LABELS.Detected);
    expect(text(element)).toContain(PANEL_COPY.verificationHeading);
  });

  /**
   * The case the disclaimer exists for: an empty answer is the one a reader could take as a clearance.
   */
  it('shows the disclaimer on an empty answer, and says an empty answer is not a clearance', async () => {
    service.outcome = { status: 'found', warnings: warnings({ warnings: [] }) };

    const element = await render();
    await checked(element);

    expect(element.querySelector('cp-notice.disclaimer .body')?.textContent?.trim()).toBe(DISCLAIMER);
    expect(text(element)).toContain(PANEL_COPY.nothingFound);
  });

  it('says when a method it could not read means the answer came from less than the whole recipe', async () => {
    service.outcome = { status: 'found', warnings: warnings({ unrecognisedTechniques: 1 }) };

    const element = await render();
    await checked(element);

    expect(text(element)).toContain(PANEL_COPY.unrecognised);
  });

  it("shows where a warning came from on request, quoting the creator's own words", async () => {
    const element = await render();
    await checked(element);

    const disclosure = buttonWith(element, PANEL_COPY.triggersHeading)!;
    const list = element.querySelector(`#${disclosure.getAttribute('aria-controls')}`) as HTMLElement;

    // Actually hidden, not merely marked so: every list in this panel is `display: grid`.
    expect(getComputedStyle(list).display).toBe('none');
    expect(disclosure.getAttribute('aria-expanded')).toBe('false');

    disclosure.click();
    await settle();

    expect(getComputedStyle(list).display).not.toBe('none');
    expect(text(element)).toContain('Pressure-can at 10 lb for 75 minutes');
    expect(text(element)).toContain(FIELD_LABELS.Step);
    expect(text(element)).toContain(TRIGGER_KIND_LABELS.Text);
  });

  it('records a stated temperature as written, and never converts it', async () => {
    service.outcome = {
      status: 'found',
      warnings: warnings({
        warnings: [
          warning({
            category: 'InternalTemperature',
            message: 'This recipe states an internal temperature.',
            statedTemperatures: [{ value: 165, scale: 'Fahrenheit', field: 'Step', itemId: 's2' }],
          }),
        ],
      }),
    };

    const element = await render();
    await checked(element);

    expect(text(element)).toContain('165°F');
    // No conversion: 74°C is what 165°F is, and computing it here would be arithmetic on a safety figure.
    expect(text(element)).not.toContain('74');
    expect(text(element)).toContain(PANEL_COPY.temperaturesNote);
  });

  it('says a check is running and keeps the button focusable', async () => {
    const element = await render();
    const button = buttonWith(element, PANEL_COPY.check)!;
    button.focus();
    service.holdNext = true;
    button.click();
    await settle();

    const working = buttonWith(element, PANEL_COPY.loading)!;
    expect(working.disabled).toBeFalse();
    expect(working.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(working);
    expect(element.querySelector('[role="status"].cp-sr-only')?.textContent).toContain(PANEL_COPY.checking);

    working.click();
    await settle();
    expect(service.calls.length).toBe(1);

    service.finish();
    await settle();
    expect(text(element)).toContain(CATEGORY_LABELS.Canning);
  });

  it('announces what was found, counting the checks that matched', async () => {
    const element = await render();
    await checked(element);

    expect(element.querySelector('[role="status"].cp-sr-only')?.textContent).toContain(foundSentence(1));
  });

  it('says that unsaved edits were not read', async () => {
    const element = await render(3, true);

    expect(text(element)).toContain(PANEL_COPY.dirty);
  });

  it('says the answer is out of date when the recipe has moved past the version it read', async () => {
    const element = await render();
    await checked(element);

    fixture.componentRef.setInput('sourceVersionNumber', 4);
    await settle();

    expect(text(element)).toContain(PANEL_COPY.stale);
    // The warning stays: it was true of the version it names, and taking it off screen is the worse failure.
    expect(text(element)).toContain(CATEGORY_LABELS.Canning);
  });

  it('answers each refusal in words a creator can act on', async () => {
    const cases: readonly [FoodSafetyWarningsOutcome, string][] = [
      [{ status: 'version_not_found' }, PANEL_COPY.versionNotFound],
      [{ status: 'not_found' }, PANEL_COPY.notFound],
      [{ status: 'unauthorized' }, PANEL_COPY.unauthorized],
      [{ status: 'unavailable' }, PANEL_COPY.unavailable],
    ];

    for (const [outcome, message] of cases) {
      service.outcome = outcome;
      const element = await render();
      await checked(element);

      expect(text(element)).toContain(message);
      expect(element.querySelector('[role="alert"]')).not.toBeNull();
    }
  });

  it('keeps a warning on screen when the next check fails', async () => {
    const element = await render();
    await checked(element);

    service.outcome = { status: 'unavailable' };
    await click(element, PANEL_COPY.recheck);

    expect(text(element)).toContain(PANEL_COPY.unavailable);
    expect(text(element)).toContain(CATEGORY_LABELS.Canning);
  });

  // ---- The claims this panel may never make ----------------------------

  /**
   * The same guard the domain holds for its own sentences (B-37), for the sentences written here.
   */
  it('says nothing about a recipe being safe or unsafe, names no hazard, and gives no instruction', () => {
    const forbidden = [
      /\bis safe\b/i,
      /\bunsafe\b/i,
      /\bdangerous\b/i,
      /\bhazard\b/i,
      /\bbotulism\b/i,
      /\bsalmonella\b/i,
      /\blisteria\b/i,
      /\bpoisoning\b/i,
      /\byou must\b/i,
      /\bmake sure (?:to|you)\b/i,
      /\bcertified?\b/i,
      /\bguarantee[ds]?\b/i,
    ];

    /**
     * The one sentence allowed to contain "is safe", because it denies it.
     *
     * Listed rather than excused by a cleverer regex: a denial and an assertion differ by one word, and a
     * pattern smart enough to tell them apart is a pattern nobody can check by reading. One named exception,
     * asserted below to actually be a denial, is reviewable.
     */
    const denials = [PANEL_COPY.nothingFound];
    expect(PANEL_COPY.nothingFound).toContain('not a statement that the recipe is safe');

    const ownCopy: string[] = [
      ...Object.values(PANEL_COPY).filter((sentence) => !denials.includes(sentence)),
      ...Object.values(CATEGORY_LABELS),
      ...Object.values(CONFIDENCE_LABELS),
      ...Object.values(FIELD_LABELS),
      ...Object.values(TRIGGER_KIND_LABELS),
      ...Object.values(EVIDENCE_STATUS_LABELS),
      foundSentence(1),
      foundSentence(3),
    ];

    for (const sentence of ownCopy) {
      for (const pattern of forbidden) {
        expect(sentence).not.toMatch(pattern);
      }
    }
  });

  /**
   * A flagged method is not a fault in the recipe, so nothing here is painted as an error — and nothing is
   * painted as a success either, which would read as a clearance.
   */
  it('paints a warning as neither an error nor a success', () => {
    for (const tone of Object.values(CONFIDENCE_TONES)) {
      expect(tone).not.toBe('error');
      expect(tone).not.toBe('success');
    }
  });
});
