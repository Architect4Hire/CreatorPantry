import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RecipeVersionHistoryEntry } from '../../models/recipe-version.models';
import { CreatedRecipe, DuplicateRecipeRequest } from '../../models/recipe.models';
import { DuplicateRecipeOutcome, RecipeService } from '../../services/recipe.service';
import { RecipeDuplicateComponent, RecipeDuplicated } from './recipe-duplicate.component';

const COPY: CreatedRecipe = {
  recipeId: 'r2',
  title: 'Skillet Cornbread — sourdough',
  status: 'Draft',
  versionId: 'v1',
  versionNumber: 1,
  createdAt: '2026-03-12T14:02:00Z',
};

function entry(versionNumber: number): RecipeVersionHistoryEntry {
  return {
    id: `v${versionNumber}`,
    versionNumber,
    source: 'CreatorEdit',
    readiness: 'Draft',
    reason: null,
    createdAt: '2026-03-12T14:02:00Z',
    createdByName: 'Sam Okafor',
    parentVersionId: versionNumber === 1 ? null : `v${versionNumber - 1}`,
    restoredFromVersionId: null,
    aiProposalId: null,
  };
}

interface DuplicateCall {
  readonly request: DuplicateRecipeRequest;
  readonly idempotencyKey: string | undefined;
}

class StubRecipeService {
  outcome: DuplicateRecipeOutcome = { status: 'created', recipe: COPY, replayed: false };

  calls: DuplicateCall[] = [];

  /** Held open so a test can assert what happens while a copy is in flight. */
  pending: ((outcome: DuplicateRecipeOutcome) => void) | null = null;

  duplicateRecipe(
    _workspaceSlug: string,
    _recipeId: string,
    request: DuplicateRecipeRequest,
    idempotencyKey?: string,
  ): Promise<DuplicateRecipeOutcome> {
    this.calls.push({ request, idempotencyKey });

    if (this.pending !== null) {
      return new Promise((resolve) => {
        this.pending = resolve;
      });
    }

    return Promise.resolve(this.outcome);
  }
}

describe('RecipeDuplicateComponent', () => {
  let service: StubRecipeService;
  let fixture: ComponentFixture<RecipeDuplicateComponent>;
  let duplicated: RecipeDuplicated[];
  let reloadRequests: number;
  let closes: number;

  async function render(version = 3): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeDuplicateComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('sourceTitle', 'Skillet Cornbread');
    fixture.componentRef.setInput('version', entry(version));

    duplicated = [];
    reloadRequests = 0;
    closes = 0;
    fixture.componentInstance.duplicated.subscribe((event) => duplicated.push(event));
    fixture.componentInstance.reloadRequested.subscribe(() => (reloadRequests += 1));
    fixture.componentInstance.closed.subscribe(() => (closes += 1));

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function buttonWith(element: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(element.querySelectorAll('button')).find((button) => button.textContent?.includes(text));
  }

  /** Types a title, because the copy cannot be submitted without one. */
  function titled(title = 'Skillet Cornbread — sourdough'): void {
    fixture.componentInstance.title.set(title);
    fixture.detectChanges();
  }

  async function submit(element: HTMLElement): Promise<void> {
    buttonWith(element, 'Duplicate')?.click();
    await settle();
  }

  beforeEach(() => {
    service = new StubRecipeService();
    TestBed.configureTestingModule({
      imports: [RecipeDuplicateComponent],
      providers: [{ provide: RecipeService, useValue: service }],
    });
  });

  // ---- What the dialog promises ----

  it('says the copy is independent, a Draft, and starts its own history', async () => {
    const element = await render(3);

    const text = element.textContent ?? '';
    expect(text).toContain('Duplicate version 3?');
    expect(text).toContain('Skillet Cornbread');
    expect(text).toContain('editing either one does nothing to the other');
    expect(text).toContain('starts its own at version 1');
    expect(text).toContain('always a Draft');
  });

  it('says media links point at the same files rather than copies of them', async () => {
    const element = await render();

    expect(element.textContent).toContain('pointing at the same files, which are not copied or re-owned');
  });

  /** The route accepts a title and a version number and nothing else; a toggle here would invent a contract. */
  it('offers no copy options, only a statement of what is copied', async () => {
    const element = await render();

    expect(element.querySelectorAll('input[type="checkbox"]').length).toBe(0);
    expect(element.querySelectorAll('input[type="radio"]').length).toBe(0);
    expect(element.querySelectorAll('select').length).toBe(0);
  });

  /** Test runs and publishing state are not copied, and are not concepts this client knows about either. */
  it('says nothing about test runs or publishing state', async () => {
    const element = await render();

    expect(element.textContent).not.toMatch(/test run|publish|publication|scheduled/i);
  });

  // ---- The request ----

  it('will not submit without a title, because the server will not invent one', async () => {
    const element = await render();

    expect(fixture.componentInstance.title()).toBe('');
    expect(buttonWith(element, 'Duplicate')?.disabled).toBeTrue();

    await submit(element);
    expect(service.calls).toEqual([]);

    titled('   ');
    expect(buttonWith(element, 'Duplicate')?.disabled).toBeTrue();
  });

  it('copies the version the row named, sending the title and nothing else', async () => {
    const element = await render(3);
    titled('Skillet Cornbread — sourdough');

    await submit(element);

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request).toEqual({
      title: 'Skillet Cornbread — sourdough',
      sourceVersionNumber: 3,
    });
  });

  it('names the current version explicitly rather than leaving the server to assume it', async () => {
    const element = await render(8);
    titled();

    await submit(element);

    expect(service.calls[0].request.sourceVersionNumber).toBe(8);
  });

  it('does not start a second copy while one is in flight', async () => {
    const element = await render();
    titled();
    service.pending = () => undefined;

    buttonWith(element, 'Duplicate')?.click();
    await settle();
    buttonWith(element, 'Duplicating…')?.click();
    await settle();

    expect(service.calls.length).toBe(1);
  });

  it('reuses one idempotency key for a retry, so a dropped response cannot leave two copies', async () => {
    const element = await render();
    titled();
    service.outcome = { status: 'unavailable' };

    await submit(element);
    await submit(element);

    expect(service.calls.length).toBe(2);
    expect(service.calls[0].idempotencyKey).toBeTruthy();
    expect(service.calls[1].idempotencyKey).toBe(service.calls[0].idempotencyKey);
  });

  it('takes a new key once the title has changed, because that is a different copy', async () => {
    const element = await render();
    titled('First idea');
    service.outcome = { status: 'unavailable' };

    await submit(element);
    titled('Second idea');
    await submit(element);

    expect(service.calls[1].idempotencyKey).not.toBe(service.calls[0].idempotencyKey);
  });

  // ---- Success ----

  it('reports the copy and the version it came from', async () => {
    const element = await render(3);
    titled();

    await submit(element);

    expect(duplicated.length).toBe(1);
    expect(duplicated[0].fromVersionNumber).toBe(3);
    expect(duplicated[0].recipe.recipeId).toBe('r2');
    expect(duplicated[0].recipe.status).toBe('Draft');
  });

  // ---- Refusals ----

  it('says the history moved on when the source version is no longer there', async () => {
    const element = await render(3);
    titled();
    service.outcome = { status: 'version_not_found', fieldErrors: { sourceVersionNumber: ['No such version.'] } };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('no longer has a version 3');
    expect(buttonWith(element, 'Reload history')).toBeTruthy();
    expect(buttonWith(element, 'Duplicate')?.disabled).toBeTrue();

    buttonWith(element, 'Reload history')?.click();
    expect(reloadRequests).toBe(1);
  });

  it('names the role a copy needs, which is a lower bar than a restore', async () => {
    const element = await render();
    titled();
    service.outcome = { status: 'forbidden' };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Contributor role');
    expect(buttonWith(element, 'Duplicate')?.disabled).toBeTrue();
  });

  it('puts a refused title’s message beside the box it came from, and offers another attempt', async () => {
    const element = await render();
    titled('x'.repeat(201));
    service.outcome = { status: 'validation_failed', fieldErrors: { title: ['A title can be at most 200 characters.'] } };

    await submit(element);

    expect(element.querySelector('cp-field .error')?.textContent).toContain('at most 200 characters');
    expect(buttonWith(element, 'Duplicate')?.disabled).toBeFalse();
    expect(fixture.componentInstance.title().length).toBe(201);
  });

  it('reports a refused version number in words, since no field on this form can fix it', async () => {
    const element = await render();
    titled();
    service.outcome = {
      status: 'validation_failed',
      fieldErrors: { sourceVersionNumber: ['A version number starts at 1.'] },
    };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('A version number starts at 1.');
  });

  /** A fresh dialog per outcome: a refusal that cannot be retried disables the button it was pressed from. */
  it('reports an unreadable recipe, a reused key and a transient failure distinctly', async () => {
    const cases: { outcome: DuplicateRecipeOutcome; says: string }[] = [
      { outcome: { status: 'not_found' }, says: 'could not be found' },
      { outcome: { status: 'idempotency_key_conflict' }, says: 'already submitted' },
      { outcome: { status: 'unavailable' }, says: 'Check your connection' },
    ];

    for (const { outcome, says } of cases) {
      const element = await render();
      titled();
      service.outcome = outcome;

      await submit(element);

      expect(element.querySelector('[role="alert"]')?.textContent).toContain(says);
    }
  });

  // ---- Leaving ----

  it('copies nothing when it is cancelled', async () => {
    const element = await render();
    titled();

    buttonWith(element, 'Cancel')?.click();
    await settle();

    expect(service.calls).toEqual([]);
    expect(closes).toBe(1);
    expect(duplicated).toEqual([]);
  });

  it('closes on Escape without copying anything', async () => {
    await render();
    titled();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(closes).toBe(1);
    expect(service.calls).toEqual([]);
  });

  it('is a modal dialog, named by its own heading, with a labelled and described title box', async () => {
    const element = await render(3);

    const dialog = element.querySelector('[role="dialog"]');
    expect(dialog?.getAttribute('aria-modal')).toBe('true');

    const labelledBy = dialog?.getAttribute('aria-labelledby');
    expect(element.querySelector(`#${labelledBy}`)?.textContent).toContain('Duplicate version 3?');

    const input = element.querySelector('input[type="text"]');
    expect(element.querySelector('label')?.getAttribute('for')).toBe(input?.id);
    expect(input?.getAttribute('maxlength')).toBe('200');

    const describedBy = input?.getAttribute('aria-describedby');
    expect(element.querySelector(`#${describedBy}`)?.textContent).toContain('never mistaken for its source');
  });
});
