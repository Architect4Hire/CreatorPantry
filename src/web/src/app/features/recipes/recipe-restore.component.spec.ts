import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RecipeVersionHistoryEntry, RestoreRecipeVersionRequest } from '../../models/recipe-version.models';
import { RecipeDetail } from '../../models/recipe.models';
import { RecipeService, RestoreRecipeVersionOutcome } from '../../services/recipe.service';
import { RecipeRestoreComponent, RecipeVersionRestored } from './recipe-restore.component';

const RECIPE_DETAIL: RecipeDetail = {
  id: 'r1',
  title: 'Chili',
  description: null,
  headnote: null,
  notes: null,
  storageNotes: null,
  attributionText: null,
  sourceUrl: null,
  cuisineId: null,
  courseId: null,
  primaryTechniqueId: null,
  prepTimeMinutes: null,
  cookTimeMinutes: null,
  restTimeMinutes: null,
  totalTimeMinutes: null,
  yieldText: null,
  yieldQuantity: null,
  yieldUnitId: null,
  status: 'Draft',
  duplicatedFrom: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  concurrencyToken: 'AAAAAAAAB9I=',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

/** The recipe as it stands after a restore wrote version `versionNumber`. */
function detailAtVersion(versionNumber: number): RecipeDetail {
  return {
    ...RECIPE_DETAIL,
    currentVersion: {
      id: `v${versionNumber}`,
      versionNumber,
      source: 'Restore',
      readiness: 'Draft',
      reason: null,
      createdAt: '2026-03-12T14:02:00Z',
    },
  };
}

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

interface RestoreCall {
  readonly versionNumber: number;
  readonly request: RestoreRecipeVersionRequest;
  readonly idempotencyKey: string | undefined;
}

class StubRecipeService {
  outcome: RestoreRecipeVersionOutcome = { status: 'restored', recipe: detailAtVersion(9), replayed: false };

  calls: RestoreCall[] = [];

  /** Held open so a test can assert what happens while a restore is in flight. */
  pending: ((outcome: RestoreRecipeVersionOutcome) => void) | null = null;

  restoreVersion(
    _workspaceSlug: string,
    _recipeId: string,
    versionNumber: number,
    request: RestoreRecipeVersionRequest,
    idempotencyKey?: string,
  ): Promise<RestoreRecipeVersionOutcome> {
    this.calls.push({ versionNumber, request, idempotencyKey });

    if (this.pending !== null) {
      return new Promise((resolve) => {
        this.pending = resolve;
      });
    }

    return Promise.resolve(this.outcome);
  }
}

describe('RecipeRestoreComponent', () => {
  let service: StubRecipeService;
  let fixture: ComponentFixture<RecipeRestoreComponent>;
  let restored: RecipeVersionRestored[];
  let reloadRequests: number;
  let closes: number;

  /** Renders the dialog for `version`, against a history whose newest version is `newest`. */
  async function render(version = 3, newest = 8, options: { dirty?: boolean; token?: string | null } = {}): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeRestoreComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('version', entry(version));
    fixture.componentRef.setInput('newestVersionNumber', newest);
    fixture.componentRef.setInput('concurrencyToken', options.token === undefined ? 'AAAAAAAAB9E=' : options.token);
    fixture.componentRef.setInput('editorIsDirty', options.dirty ?? false);

    restored = [];
    reloadRequests = 0;
    closes = 0;
    fixture.componentInstance.restored.subscribe((event) => restored.push(event));
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

  async function submit(element: HTMLElement): Promise<void> {
    buttonWith(element, 'Restore as a new version')?.click();
    await settle();
  }

  beforeEach(() => {
    service = new StubRecipeService();
    TestBed.configureTestingModule({
      imports: [RecipeRestoreComponent],
      providers: [{ provide: RecipeService, useValue: service }],
    });
  });

  // ---- What the dialog promises ----

  it('says the restore writes a new version, and never that the chosen one becomes current', async () => {
    const element = await render(3, 8);

    const text = element.textContent ?? '';
    expect(text).toContain('Restore version 3?');
    expect(text).toContain('new version 9');
    expect(text).toContain('stays exactly where it is');
    expect(text).toContain('Restore as a new version');

    // The wording a creator would read as "the versions after this one disappear". None of it may appear.
    expect(text).not.toMatch(/becomes? current|made current|rolled? back|reinstat|revert|undo/i);
    expect(text).not.toMatch(/delete|remove[ds]? from the history/i);
  });

  it('says plainly that nothing in the history is removed or renumbered', async () => {
    const element = await render();

    expect(element.textContent).toContain('Nothing in the history is removed, renumbered or rewritten.');
  });

  it('warns that restoring the current version would write nothing', async () => {
    const element = await render(8, 8);

    expect(element.querySelector('.notice--warning')?.textContent).toContain('already what this recipe says');
  });

  it('does not warn about the current version when an older one was chosen', async () => {
    const element = await render(3, 8);

    expect(element.textContent).not.toContain('already what this recipe says');
  });

  it('warns that unsaved edits are lost, and only when there are some', async () => {
    const dirty = await render(3, 8, { dirty: true });
    expect(dirty.textContent).toContain('unsaved edits');

    const clean = await render(3, 8, { dirty: false });
    expect(clean.textContent).not.toContain('unsaved edits');
  });

  // ---- The request ----

  it('restores the version it names, quoting the token it was given', async () => {
    const element = await render(3, 8);
    fixture.componentInstance.reason.set('The reworded headnote read worse.');
    fixture.detectChanges();

    await submit(element);

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].versionNumber).toBe(3);
    expect(service.calls[0].request.expectedConcurrencyToken).toBe('AAAAAAAAB9E=');
    expect(service.calls[0].request.reason).toBe('The reworded headnote read worse.');
  });

  it('submits an untouched reason as nothing to record', async () => {
    const element = await render();

    await submit(element);

    expect(service.calls[0].request.reason).toBe('');
  });

  it('will not submit without a token, because nothing would check the restore', async () => {
    const element = await render(3, 8, { token: null });

    await submit(element);

    expect(service.calls).toEqual([]);

    // Its own state, not a network failure: retrying would fail identically forever, so a reload is offered.
    expect(element.textContent).toContain("hasn't finished loading");
    expect(buttonWith(element, 'Reload recipe')).toBeTruthy();
    expect(buttonWith(element, 'Restore as a new version')?.disabled).toBeTrue();
  });

  it('does not start a second restore while one is in flight', async () => {
    const element = await render();
    service.pending = () => undefined;

    buttonWith(element, 'Restore as a new version')?.click();
    await settle();
    buttonWith(element, 'Restoring…')?.click();
    await settle();

    expect(service.calls.length).toBe(1);
  });

  it('reuses one idempotency key for a retry of the same restore, so it cannot write two versions', async () => {
    const element = await render();
    service.outcome = { status: 'unavailable' };

    await submit(element);
    await submit(element);

    expect(service.calls.length).toBe(2);
    expect(service.calls[0].idempotencyKey).toBeTruthy();
    expect(service.calls[1].idempotencyKey).toBe(service.calls[0].idempotencyKey);
  });

  it('takes a new key once the reason has changed, because that is a different request', async () => {
    const element = await render();
    service.outcome = { status: 'unavailable' };

    await submit(element);
    fixture.componentInstance.reason.set('Second thoughts.');
    fixture.detectChanges();
    await submit(element);

    expect(service.calls[1].idempotencyKey).not.toBe(service.calls[0].idempotencyKey);
  });

  // ---- Success ----

  it('reports the version that was restored and the version that was written', async () => {
    const element = await render(3, 8);
    service.outcome = { status: 'restored', recipe: detailAtVersion(9), replayed: false };

    await submit(element);

    expect(restored.length).toBe(1);
    expect(restored[0].fromVersionNumber).toBe(3);
    expect(restored[0].newVersionNumber).toBe(9);
    expect(restored[0].recipe.concurrencyToken).toBe('AAAAAAAAB9I=');
  });

  /** The server writes no version when the restore would change nothing, and says so by not moving. */
  it('reports a restore that changed nothing as having written no version', async () => {
    const element = await render(8, 8);
    service.outcome = { status: 'restored', recipe: detailAtVersion(8), replayed: false };

    await submit(element);

    expect(restored[0].newVersionNumber).toBeNull();
  });

  // ---- Refusals ----

  it('recovers from a stale token by offering a reload, not another attempt', async () => {
    const element = await render();
    service.outcome = { status: 'conflict' };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('changed while this dialog was open');
    expect(element.textContent).toContain('nothing was restored');
    expect(buttonWith(element, 'Reload recipe')).toBeTruthy();
    expect(buttonWith(element, 'Restore as a new version')?.disabled).toBeTrue();

    buttonWith(element, 'Reload recipe')?.click();
    expect(reloadRequests).toBe(1);
  });

  it('keeps the creator’s reason through a refusal, so nothing they wrote is lost', async () => {
    const element = await render();
    fixture.componentInstance.reason.set('Version 3 read better.');
    service.outcome = { status: 'conflict' };

    await submit(element);

    expect(fixture.componentInstance.reason()).toBe('Version 3 read better.');
  });

  it('tells an archived recipe apart from a stale one, and offers neither retry nor reload', async () => {
    const element = await render();
    service.outcome = { status: 'archived_conflict' };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('archived');
    expect(buttonWith(element, 'Reload recipe')).toBeUndefined();
    expect(buttonWith(element, 'Restore as a new version')?.disabled).toBeTrue();
  });

  it('says the history moved on when the version is no longer there', async () => {
    const element = await render(3, 8);
    service.outcome = { status: 'version_not_found', fieldErrors: { versionNumber: ['No such version.'] } };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('no longer has a version 3');
    expect(buttonWith(element, 'Reload recipe')).toBeTruthy();
  });

  it('names the role a restore needs when the server refuses it', async () => {
    const element = await render();
    service.outcome = { status: 'forbidden' };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Editor role');
    expect(buttonWith(element, 'Restore as a new version')?.disabled).toBeTrue();
  });

  it('puts a refused reason’s message beside the box it came from', async () => {
    const element = await render();
    service.outcome = { status: 'validation_failed', fieldErrors: { reason: ['That reason is too long.'] } };

    await submit(element);

    expect(element.querySelector('cp-field .error')?.textContent).toContain('That reason is too long.');
    expect(buttonWith(element, 'Restore as a new version')).toBeTruthy();
  });

  it('offers another attempt after a transient failure', async () => {
    const element = await render();
    service.outcome = { status: 'unavailable' };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Check your connection');
    expect(buttonWith(element, 'Restore as a new version')).toBeTruthy();
  });

  it('explains a reused idempotency key rather than looping on it', async () => {
    const element = await render();
    service.outcome = { status: 'idempotency_key_conflict' };

    await submit(element);

    expect(element.querySelector('[role="alert"]')?.textContent).toContain('already submitted');
  });

  // ---- Leaving ----

  it('restores nothing when it is cancelled', async () => {
    const element = await render();

    buttonWith(element, 'Cancel')?.click();
    await settle();

    expect(service.calls).toEqual([]);
    expect(closes).toBe(1);
    expect(restored).toEqual([]);
  });

  it('is a modal dialog, named by its own heading', async () => {
    const element = await render(3, 8);

    const dialog = element.querySelector('[role="dialog"]');
    expect(dialog?.getAttribute('aria-modal')).toBe('true');

    const labelledBy = dialog?.getAttribute('aria-labelledby');
    expect(labelledBy).toBeTruthy();
    expect(element.querySelector(`#${labelledBy}`)?.textContent).toContain('Restore version 3?');
  });

  it('labels the reason box, describes it, and bounds it to what the server accepts', async () => {
    const element = await render();

    const textarea = element.querySelector('textarea');
    const label = element.querySelector('label');
    expect(label?.getAttribute('for')).toBe(textarea?.id);
    expect(textarea?.getAttribute('maxlength')).toBe('1000');

    // The hint is rendered by CpField with this id and wired to nothing, so the control has to point at it.
    const describedBy = textarea?.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(element.querySelector(`#${describedBy}`)?.textContent).toContain('Recorded on the new version');
  });

  it('closes on Escape without restoring anything', async () => {
    await render();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(closes).toBe(1);
    expect(service.calls).toEqual([]);
  });
});
