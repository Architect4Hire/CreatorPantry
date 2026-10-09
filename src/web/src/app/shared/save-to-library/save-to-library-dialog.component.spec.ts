import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { DamCreatedAsset, DamKeepPrompt, DamKeepRecipe } from '../../models/dam-asset.models';
import { RecipeSearchQuery, RecipeSummary } from '../../models/recipe.models';
import { DamAssetKeepOutcome, DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService, StagedImagePreviewOutcome } from '../../services/generated-image.service';
import { RecipeSearchOutcome, RecipeService } from '../../services/recipe.service';
import { WorkspaceTagService, WorkspaceTagsOutcome } from '../../services/workspace-tag.service';
import { SaveToLibraryDialogComponent, SaveToLibraryProgress } from './save-to-library-dialog.component';

function asset(overrides: Partial<DamCreatedAsset> = {}): DamCreatedAsset {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    kind: 'AiGenerated',
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 2048,
    sourceGeneratedImageId: 'g1',
    promptRecordId: null,
    recipeId: null,
    createdAt: '2026-10-09T12:00:00+00:00',
    ...overrides,
  };
}

function recipe(id: string, title: string): RecipeSummary {
  return {
    id,
    title,
    description: null,
    status: 'Draft',
    cuisineId: null,
    courseId: null,
    createdAt: '2026-10-01T12:00:00+00:00',
    updatedAt: '2026-10-01T12:00:00+00:00',
    latestVersionNumber: 1,
    latestVersionReadiness: null,
    hasUnmatchedIngredients: false,
  };
}

const PROMPT: DamKeepPrompt = {
  channelKey: 'instagram',
  imageKind: 'Hero',
  text: 'Overhead shot of soda bread on linen.',
  source: 'Manual',
};

@Component({
  standalone: true,
  imports: [SaveToLibraryDialogComponent],
  template: `
    <cp-save-to-library-dialog
      [open]="open()"
      [workspaceSlug]="slug()"
      [generatedImageId]="imageId()"
      [defaultTitle]="defaultTitle()"
      [defaultRecipe]="defaultRecipe()"
      [prompt]="prompt()"
      [altTextSuggestion]="suggestion()"
      (saved)="saved.push($event)"
      (progress)="progress.push($event)"
      (imageGone)="gone = gone + 1"
      (closed)="onClosed()"
    />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly slug = signal('cozy-fall');
  readonly imageId = signal('g1');
  readonly defaultTitle = signal('Soda bread hero');
  readonly defaultRecipe = signal<DamKeepRecipe | null>(null);
  readonly prompt = signal<DamKeepPrompt | null>(null);
  readonly suggestion = signal<string | null>(null);
  readonly saved: DamCreatedAsset[] = [];
  readonly progress: SaveToLibraryProgress[] = [];
  gone = 0;
  closes = 0;

  onClosed(): void {
    this.closes += 1;
    this.open.set(false);
  }
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('SaveToLibraryDialogComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let keepSpy: jasmine.Spy<(slug: string, body: Record<string, unknown>, key: string) => Promise<DamAssetKeepOutcome>>;
  let previewSpy: jasmine.Spy<(slug: string, id: string) => Observable<StagedImagePreviewOutcome>>;
  let searchSpy: jasmine.Spy<(slug: string, query: RecipeSearchQuery) => Observable<RecipeSearchOutcome>>;
  let tagsSpy: jasmine.Spy<(slug: string) => Promise<WorkspaceTagsOutcome>>;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function field<T extends HTMLElement = HTMLInputElement>(id: string): T {
    const found = root().querySelector<T>(`[id="cp-save-library-${id}"]`);
    if (!found) throw new Error(`no field "${id}"`);
    return found;
  }

  function type(id: string, value: string): void {
    const control = field<HTMLInputElement | HTMLTextAreaElement>(id);
    control.value = value;
    control.dispatchEvent(new Event('input'));
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim().startsWith(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function describedBy(id: string): string {
    return (field(id).getAttribute('aria-describedby') ?? '')
      .split(' ')
      .filter(Boolean)
      .map((each) => root().querySelector(`[id="${each}"]`)?.textContent ?? '')
      .join(' ');
  }

  function alert(): string {
    return Array.from(root().querySelectorAll('[role="alert"]'))
      .map((each) => each.textContent ?? '')
      .join(' ');
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  /** Pick in a combobox the way a keyboard user does: type, arrow down, Enter. */
  async function pick(id: string, name: string): Promise<void> {
    const input = field(id);
    input.focus();
    input.value = name;
    input.dispatchEvent(new Event('input'));
    await settle();
    // Long enough for the recipe search, which waits for typing to rest.
    await delay(300);
    await settle();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    await settle();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await settle();
  }

  function sentBody(call = keepSpy.calls.count() - 1): Record<string, unknown> {
    return keepSpy.calls.argsFor(call)[1];
  }

  async function create(setup: (host: HostComponent) => void = () => undefined): Promise<void> {
    keepSpy = jasmine.createSpy('keepGeneratedImage').and.resolveTo({ status: 'saved', asset: asset() });
    previewSpy = jasmine
      .createSpy('preview')
      .and.returnValue(of<StagedImagePreviewOutcome>({ status: 'found', bytes: new Blob(['x'], { type: 'image/png' }) }));
    searchSpy = jasmine.createSpy('searchRecipes').and.callFake((_slug: string, query: RecipeSearchQuery) => {
      const items = [recipe('r1', 'Soda bread'), recipe('r2', 'Pumpkin soup')].filter((each) =>
        each.title.toLowerCase().includes(query.search.toLowerCase()),
      );

      return of<RecipeSearchOutcome>({ status: 'found', page: { items, nextCursor: null, totalCount: items.length } });
    });
    tagsSpy = jasmine.createSpy('list').and.resolveTo({
      status: 'found',
      tags: [
        { id: 't1', name: 'Weeknight' },
        { id: 't2', name: 'Baking' },
      ],
    });
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        { provide: DamAssetService, useValue: { keepGeneratedImage: keepSpy } },
        { provide: GeneratedImageService, useValue: { preview: previewSpy } },
        { provide: RecipeService, useValue: { searchRecipes: searchSpy } },
        { provide: WorkspaceTagService, useValue: { list: tagsSpy } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    setup(host);
    await settle();
  }

  // ---- Opening ----

  it('opens as a named modal dialog showing the picture, with the title from the context and no alt text', async () => {
    await create();

    const dialog = root().querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(root().querySelector(`#${dialog.getAttribute('aria-labelledby')}`)?.textContent).toContain('Save to library');

    expect(previewSpy).toHaveBeenCalledOnceWith('cozy-fall', 'g1');
    expect(root().querySelector('.preview img')?.getAttribute('src')).toMatch(/^blob:/);

    expect(field('title').value).toBe('Soda bread hero');
    expect(field('title').getAttribute('aria-required')).toBe('true');
    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('');
    expect(text()).toContain('Only the title is needed.');
    expect(text()).not.toContain('(optional)');
  });

  it('never fills alt text from the prompt, the title or the recipe', async () => {
    await create((each) => {
      each.prompt.set(PROMPT);
      each.defaultRecipe.set({ id: 'r1', title: 'Soda bread' });
    });

    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('');
    expect(text()).not.toContain('Generated');

    button('Save to library').click();
    await settle();
    expect((sentBody()['metadata'] as Record<string, unknown>)['altText']).toBeUndefined();
  });

  it('marks alt text from a reading of the picture as generated, until the creator edits it', async () => {
    await create((each) => each.suggestion.set('A round loaf on a linen cloth.'));

    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('A round loaf on a linen cloth.');
    expect(root().querySelector('.generated')?.textContent).toContain('Generated');
    expect(root().querySelector('.generated')?.textContent).toContain('not by you');
    expect(field<HTMLTextAreaElement>('alt-text').readOnly).toBeFalse();

    type('alt-text', 'A round soda loaf, scored, on linen.');
    await settle();
    expect(root().querySelector('.generated')?.textContent?.trim()).toBe('');

    button('Save to library').click();
    await settle();
    expect((sentBody()['metadata'] as Record<string, unknown>)['altText']).toBe('A round soda loaf, scored, on linen.');
  });

  it('still lets the picture be saved when it cannot be shown', async () => {
    await create();
    previewSpy.and.returnValue(of<StagedImagePreviewOutcome>({ status: 'unavailable' }));

    button('Cancel').click();
    await settle();
    host.open.set(true);
    await settle();

    expect(text()).toContain('could not be loaded. You can still save it.');
    button('Try again').click();
    await settle();
    expect(previewSpy).toHaveBeenCalledTimes(3);

    button('Save to library').click();
    await settle();
    expect(keepSpy).toHaveBeenCalledTimes(1);
  });

  // ---- Saving ----

  it('saves the picture to this workspace with what was entered, then tells the page and closes', async () => {
    await create((each) => each.defaultRecipe.set({ id: 'r1', title: 'Soda bread' }));

    type('title', ' Soda bread, overhead ');
    type('alt-text', 'A round loaf on linen.');
    await settle();
    await pick('add-tag', 'Baking');
    button('Add tag').click();
    await settle();

    keepSpy.and.resolveTo({ status: 'saved', asset: asset({ title: 'Soda bread, overhead', recipeId: 'r1' }) });
    button('Save to library').click();
    await settle();

    const [slug, body, key] = keepSpy.calls.mostRecent().args;
    expect(slug).toBe('cozy-fall');
    expect(body).toEqual({
      generatedImageId: 'g1',
      metadata: { title: 'Soda bread, overhead', altText: 'A round loaf on linen.', workspaceTagIds: ['t2'] },
      recipeLink: { recipeId: 'r1' },
    });
    expect(key.length).toBeGreaterThan(10);

    expect(host.saved.map((each) => each.id)).toEqual(['a1']);
    expect(host.closes).toBe(1);
  });

  it('offers the recipe from the context as the link, and lets it be removed or another be found', async () => {
    await create((each) => each.defaultRecipe.set({ id: 'r1', title: 'Soda bread' }));

    expect(root().querySelector('.linked')?.textContent).toContain('Soda bread');
    button('Remove').click();
    await settle();

    await pick('recipe', 'Pumpkin');
    const queries = searchSpy.calls.allArgs().map((args) => args[1]);
    expect(queries.map((query) => query.search)).toContain('Pumpkin');
    expect(queries.every((query) => query.statuses.length > 0 && !query.statuses.includes('Archived'))).toBeTrue();
    expect(root().querySelector('.linked')?.textContent).toContain('Pumpkin soup');

    keepSpy.and.resolveTo({ status: 'saved', asset: asset({ recipeId: 'r2' }) });
    button('Save to library').click();
    await settle();
    expect(sentBody()['recipeLink']).toEqual({ recipeId: 'r2' });
  });

  it('sends no recipe link when none is chosen', async () => {
    await create();

    button('Save to library').click();
    await settle();

    expect(Object.keys(sentBody())).not.toContain('recipeLink');
  });

  it('keeps the prompt by default when there is one, and leaves it out when unticked', async () => {
    await create((each) => each.prompt.set(PROMPT));
    keepSpy.and.resolveTo({ status: 'saved', asset: asset({ promptRecordId: 'p1' }) });

    expect(field('keep-prompt').checked).toBeTrue();
    button('Save to library').click();
    await settle();
    expect(sentBody()['prompt']).toEqual({ ...PROMPT, generatedImageId: 'g1' });

    host.open.set(true);
    await settle();
    field('keep-prompt').click();
    await settle();
    keepSpy.and.resolveTo({ status: 'saved', asset: asset() });
    button('Save to library').click();
    await settle();
    expect(Object.keys(sentBody())).not.toContain('prompt');
    expect(host.closes).toBe(2);
  });

  it('offers nothing about a prompt when the surface has none', async () => {
    await create();

    expect(root().querySelector('[id="cp-save-library-keep-prompt"]')).toBeNull();
    expect(text()).not.toContain('prompt library');
  });

  it('saves on Enter in a text box', async () => {
    await create();

    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();

    expect(keepSpy).toHaveBeenCalledTimes(1);
  });

  // ---- One picture, one asset ----

  it('sends one request for a double press, says it is saving, and cannot be closed meanwhile', async () => {
    await create();
    let finish: (outcome: DamAssetKeepOutcome) => void = () => undefined;
    keepSpy.and.returnValue(new Promise<DamAssetKeepOutcome>((resolve) => (finish = resolve)));

    const save = button('Save to library');
    save.click();
    save.click();
    (root().querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();

    expect(button('Saving').disabled).toBeTrue();
    expect(button('Cancel').disabled).toBeTrue();
    button('Cancel').click();
    await settle();
    expect(keepSpy).toHaveBeenCalledTimes(1);
    expect(host.closes).toBe(0);

    finish({ status: 'saved', asset: asset() });
    await settle();
    expect(host.saved.length).toBe(1);
    expect(host.closes).toBe(1);
  });

  it('retries the same entry under the same key, and a changed entry under a new one', async () => {
    await create((each) => each.prompt.set(PROMPT));
    keepSpy.and.resolveTo({ status: 'unavailable' });

    button('Save to library').click();
    await settle();
    expect(alert()).toContain('still here, so try again');
    button('Save to library').click();
    await settle();

    type('title', 'Another name');
    await settle();
    button('Save to library').click();
    await settle();

    // The prompt is not part of what the server fingerprints, so the key must not outlive that choice either.
    field('keep-prompt').click();
    await settle();
    button('Save to library').click();
    await settle();

    const keys = keepSpy.calls.allArgs().map((args) => args[2]);
    expect(keys[0]).toBe(keys[1]);
    expect(keys[2]).not.toBe(keys[1]);
    expect(keys[3]).not.toBe(keys[2]);
  });

  it('asks again under a new key when the server says the key was spent', async () => {
    await create();
    keepSpy.and.resolveTo({ status: 'key_reused' });

    button('Save to library').click();
    await settle();
    button('Save to library').click();
    await settle();

    const keys = keepSpy.calls.allArgs().map((args) => args[2]);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('treats a picture already in the library as saved, says nothing here was applied, and opens it', async () => {
    await create();
    keepSpy.and.resolveTo({ status: 'saved', asset: asset({ id: 'a9', title: 'Kept last week' }) });

    type('title', 'A new name for it');
    await settle();
    button('Save to library').click();
    await settle();
    await delay(10);
    fixture.detectChanges();

    expect(host.saved.map((each) => each.id)).toEqual(['a9']);
    expect(host.closes).toBe(0);
    expect(root().querySelector('form')).toBeNull();

    const notice = root().querySelector('[role="status"]');
    expect(notice?.textContent).toContain('already in your library as “Kept last week”');
    expect(notice?.textContent).toContain('was not applied');

    const link = field<HTMLAnchorElement>('open-asset');
    expect(link.getAttribute('href')).toBe('/cozy-fall/dam/a9');
    expect(document.activeElement).toBe(link);

    // Nothing left to lose, so closing does not ask.
    button('Close').click();
    await settle();
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.closes).toBe(1);
    expect(keepSpy).toHaveBeenCalledTimes(1);
  });

  it('reads a sent prompt that was not recorded as the picture having been kept before', async () => {
    await create((each) => each.prompt.set(PROMPT));
    keepSpy.and.resolveTo({ status: 'saved', asset: asset({ promptRecordId: null }) });

    button('Save to library').click();
    await settle();

    expect(text()).toContain('already in your library');
    expect(host.saved.length).toBe(1);
  });

  // ---- Validation ----

  it('refuses an empty title beside the field, focuses it, and sends nothing', async () => {
    await create((each) => each.defaultTitle.set(''));

    button('Save to library').click();
    await settle();
    await delay(10);
    fixture.detectChanges();

    expect(keepSpy).not.toHaveBeenCalled();
    expect(describedBy('title')).toContain('needs a title');
    expect(document.activeElement).toBe(field('title'));
  });

  it("puts the server's refusal of one field beside that field and keeps the rest as entered", async () => {
    await create();
    keepSpy.and.resolveTo({
      status: 'invalid',
      message: 'The asset could not be created.',
      fieldErrors: { WorkspaceTagIds: ['One of those tags is not in this workspace.'] },
    });

    type('alt-text', 'A round loaf on linen.');
    await settle();
    await pick('add-tag', 'Weeknight');
    button('Add tag').click();
    await settle();
    button('Save to library').click();
    await settle();
    await delay(10);
    fixture.detectChanges();

    expect(describedBy('add-tag')).toContain('One of those tags is not in this workspace.');
    expect(describedBy('title')).not.toContain('tags');
    expect(alert()).toContain('could not be saved as entered. Nothing was saved.');
    expect(document.activeElement).toBe(field('add-tag'));

    expect(field('title').value).toBe('Soda bread hero');
    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('A round loaf on linen.');
    expect(host.saved).toEqual([]);
    expect(host.closes).toBe(0);
  });

  it('puts a refused recipe on the recipe, where removing it is the remedy', async () => {
    await create((each) => each.defaultRecipe.set({ id: 'r1', title: 'Soda bread' }));
    keepSpy.and.resolveTo({
      status: 'invalid',
      message: 'The asset could not be created.',
      fieldErrors: { recipeId: ['That recipe could not be found in this workspace.'] },
    });

    button('Save to library').click();
    await settle();
    await delay(10);
    fixture.detectChanges();

    expect(alert()).toContain('That recipe could not be found in this workspace.');
    expect(document.activeElement).toBe(field<HTMLButtonElement>('recipe-remove'));
  });

  it("shows the server's own sentence when it names no field this form has", async () => {
    await create();
    keepSpy.and.resolveTo({
      status: 'invalid',
      message: 'The asset could not be created.',
      fieldErrors: { GeneratedImageId: ['A generated image id is required.'] },
    });

    button('Save to library').click();
    await settle();

    expect(alert()).toContain('The asset could not be created.');
  });

  // ---- Refusals ----

  it('says a refused prompt saved nothing, on the prompt, and how to save the picture alone', async () => {
    await create((each) => each.prompt.set(PROMPT));
    keepSpy.and.resolveTo({ status: 'prompt_refused', message: 'The prompt could not be saved.' });

    button('Save to library').click();
    await settle();

    expect(alert()).toContain('The prompt could not be saved.');
    expect(alert()).toContain('nothing was saved');
    expect(alert()).toContain('Untick this');
    expect(host.saved).toEqual([]);

    field('keep-prompt').click();
    await settle();
    expect(alert()).not.toContain('The prompt could not be saved.');
  });

  it('saves nothing for a picture that is no longer there, says so, and tells the page', async () => {
    await create();
    keepSpy.and.resolveTo({ status: 'image_gone' });

    type('alt-text', 'A round loaf.');
    await settle();
    button('Save to library').click();
    await settle();

    expect(alert()).toContain('can no longer be saved');
    expect(host.gone).toBe(1);
    expect(host.saved).toEqual([]);
    expect(button('Save to library').disabled).toBeTrue();

    button('Cancel').click();
    await settle();
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.closes).toBe(1);
  });

  it('explains a refusal for lack of access, keeping the entry', async () => {
    await create();
    keepSpy.and.resolveTo({ status: 'forbidden' });

    button('Save to library').click();
    await settle();

    expect(alert()).toContain('Contributor access');
    expect(field('title').value).toBe('Soda bread hero');
    expect(host.closes).toBe(0);
  });

  // ---- What the surface behind is told (AF.4.2) ----

  it('reports a save as it starts and as it fails, so the picture behind can say where it stands', async () => {
    await create();
    keepSpy.and.resolveTo({ status: 'forbidden' });

    button('Save to library').click();
    await settle();

    expect(host.progress).toEqual([
      { state: 'saving' },
      { state: 'failed', problem: 'You need Contributor access in this workspace to save a picture. Nothing was saved.' },
    ]);
  });

  it('reports a save that lands as the asset alone, because the picture is no longer waiting on anything', async () => {
    await create();

    button('Save to library').click();
    await settle();

    expect(host.progress).toEqual([{ state: 'saving' }]);
    expect(host.saved).toEqual([asset()]);
  });

  it('reports a picture that has gone as gone rather than as a save to try again', async () => {
    await create();
    keepSpy.and.resolveTo({ status: 'image_gone' });

    button('Save to library').click();
    await settle();

    expect(host.progress).toEqual([{ state: 'saving' }]);
    expect(host.gone).toBe(1);
  });

  // ---- Tags ----

  it('still saves when the tags cannot be loaded, and offers to load them again', async () => {
    await create();
    tagsSpy.and.resolveTo({ status: 'unavailable' });
    button('Cancel').click();
    await settle();
    host.open.set(true);
    await settle();

    expect(text()).toContain("Your tags couldn't be loaded");
    tagsSpy.and.resolveTo({ status: 'found', tags: [{ id: 't1', name: 'Weeknight' }] });
    button('Try again').click();
    await settle();
    expect(root().querySelector('[id="cp-save-library-add-tag"]')).not.toBeNull();

    button('Save to library').click();
    await settle();
    expect(keepSpy).toHaveBeenCalledTimes(1);
  });

  // ---- Cancel ----

  it('closes at once when nothing was changed from the defaults', async () => {
    await create((each) => each.defaultRecipe.set({ id: 'r1', title: 'Soda bread' }));

    button('Cancel').click();
    await settle();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(host.closes).toBe(1);
    expect(keepSpy).not.toHaveBeenCalled();
  });

  it('asks before losing what was entered, and stays when the answer is no', async () => {
    await create();
    confirmSpy.and.resolveTo(false);

    type('alt-text', 'Half a thought');
    await settle();
    button('Cancel').click();
    await settle();

    expect(confirmSpy.calls.mostRecent().args[0].title).toBe('Close without saving?');
    expect(host.closes).toBe(0);
    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('Half a thought');

    confirmSpy.and.resolveTo(true);
    (root().querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click();
    await settle();
    expect(host.closes).toBe(1);
  });

  it('starts from the defaults each time it opens, never carrying an entry from one picture to the next', async () => {
    await create();

    type('alt-text', 'typed-for-g1');
    await settle();
    button('Cancel').click();
    await settle();

    host.slug.set('other-kitchen');
    host.imageId.set('g7');
    host.defaultTitle.set('Pumpkin soup');
    host.open.set(true);
    await settle();

    expect(field('title').value).toBe('Pumpkin soup');
    expect(field<HTMLTextAreaElement>('alt-text').value).toBe('');
    expect(previewSpy.calls.mostRecent().args).toEqual(['other-kitchen', 'g7']);

    keepSpy.and.resolveTo({ status: 'saved', asset: asset({ title: 'Pumpkin soup' }) });
    button('Save to library').click();
    await settle();

    expect(keepSpy.calls.mostRecent().args[0]).toBe('other-kitchen');
    expect(sentBody()['generatedImageId']).toBe('g7');
    expect(JSON.stringify(sentBody())).not.toContain('typed-for-g1');
  });
});
