import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { VisibilityService } from '../../core/visibility.service';
import { DamAssetSearchQuery } from '../../models/dam-asset.models';
import { LinkRecipeAssetOutcome, LinkRecipeAssetRequest, RecipeMediaStep } from '../../models/recipe-asset-link.models';
import { RecipeDetail } from '../../models/recipe.models';
import {
  DamAssetContentOutcome,
  DamAssetDetailOutcome,
  DamAssetSearchOutcome,
  DamAssetService,
} from '../../services/dam-asset.service';
import { RecipeAssetLinkService } from '../../services/recipe-asset-link.service';
import { RecipeMediaLinkDialogComponent } from './recipe-media-link-dialog.component';
import { MEDIA_STEPS, assetDetail, assetSummary, mediaRecipe } from './recipe-media.testing';

@Component({
  standalone: true,
  imports: [RecipeMediaLinkDialogComponent],
  template: `
    <cp-recipe-media-link-dialog
      [open]="open()"
      workspaceSlug="cozy-fall"
      recipeId="r1"
      [concurrencyToken]="token()"
      [steps]="steps()"
      [heroTaken]="heroTaken()"
      (linked)="linked.push($event)"
      (closed)="closes = closes + 1"
      (reloadRequested)="reloads = reloads + 1"
    />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly token = signal<string | null>('token-1');
  readonly steps = signal<readonly RecipeMediaStep[]>(MEDIA_STEPS);
  readonly heroTaken = signal(false);
  readonly linked: RecipeDetail[] = [];
  closes = 0;
  reloads = 0;
}

const delay = (ms = 0): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('RecipeMediaLinkDialogComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let searchSpy: jasmine.Spy<(slug: string, query: DamAssetSearchQuery) => Observable<DamAssetSearchOutcome>>;
  let detailSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetDetailOutcome>>;
  let contentSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetContentOutcome>>;
  let versionContentSpy: jasmine.Spy<(slug: string, id: string, version: number) => Observable<DamAssetContentOutcome>>;
  let linkSpy: jasmine.Spy<
    (slug: string, recipeId: string, request: LinkRecipeAssetRequest, key: string) => Promise<LinkRecipeAssetOutcome>
  >;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return (root().textContent ?? '').replace(/\s+/g, ' ');
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find(
      (each) => each.textContent?.trim().startsWith(label) || each.getAttribute('aria-label') === label,
    );
    if (!match) throw new Error(`no button "${label}"`);
    return match;
  }

  function radio(idSuffix: string): HTMLInputElement {
    const found = root().querySelector<HTMLInputElement>(`input[id$="${idSuffix}"]`);
    if (!found) throw new Error(`no choice "${idSuffix}"`);
    return found;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await delay();
    fixture.detectChanges();
  }

  async function pick(idSuffix: string): Promise<void> {
    const control = radio(idSuffix);
    control.checked = true;
    control.dispatchEvent(new Event('change'));
    await settle();
  }

  async function choose(title = 'Soda bread hero'): Promise<void> {
    button(`Choose ${title}`).click();
    await settle();
  }

  async function submit(): Promise<void> {
    button('Link picture').click();
    await settle();
  }

  function found(items = [assetSummary()], nextCursor: string | null = null, totalCount: number | null = items.length) {
    return of<DamAssetSearchOutcome>({ status: 'found', page: { items, nextCursor, totalCount } });
  }

  beforeEach(async () => {
    searchSpy = jasmine.createSpy('search').and.callFake(() => found());
    detailSpy = jasmine.createSpy('detail').and.callFake(() => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({}, 3) }));
    contentSpy = jasmine.createSpy('content').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));
    versionContentSpy = jasmine.createSpy('versionContent').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));
    linkSpy = jasmine.createSpy('link').and.resolveTo({ status: 'linked', recipe: mediaRecipe(), replayed: false });
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        {
          provide: DamAssetService,
          useValue: { search: searchSpy, detail: detailSpy, content: contentSpy, versionContent: versionContentSpy },
        },
        { provide: RecipeAssetLinkService, useValue: { link: linkSpy } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await settle();
  });

  describe('browsing', () => {
    it('opens on the library, newest first, with nothing filtered', () => {
      expect(searchSpy).toHaveBeenCalledOnceWith('cozy-fall', {
        search: '',
        channel: null,
        day: null,
        sort: 'RecentlyAdded',
        cursor: null,
      });
      expect(text()).toContain('Add a picture from the library');
      expect(text()).toContain('Soda bread hero');
      expect(text()).toContain('1 version');
    });

    it('names each choose button by its picture and announces how many are shown', () => {
      expect(button('Choose Soda bread hero').textContent?.trim()).toBe('Choose');
      expect(root().querySelector('[role="status"].cp-sr-only')?.textContent).toContain('1 of 1 pictures shown.');
    });

    it('offers nothing that would change the library', () => {
      const labels = Array.from(root().querySelectorAll('button, a')).map((each) => (each.textContent ?? '').trim().toLowerCase());

      expect(labels.some((label) => /upload|edit|delete|remove/.test(label))).toBeFalse();
      expect(root().querySelector('input[type="file"]')).toBeNull();
    });

    it('searches with what was typed on Enter', async () => {
      const input = root().querySelector<HTMLInputElement>('#cp-dam-picker-search')!;
      input.value = 'loaf';
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      await settle();

      expect(searchSpy.calls.mostRecent().args[1].search).toBe('loaf');
    });

    it('says an empty library and an empty search differently', async () => {
      searchSpy.and.callFake(() => found([]));
      host.open.set(false);
      await settle();
      host.open.set(true);
      await settle();

      expect(text()).toContain('The library has no pictures yet');

      const input = root().querySelector<HTMLInputElement>('#cp-dam-picker-search')!;
      input.value = 'zzz';
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
      await settle();

      expect(text()).toContain('No pictures match that search');

      button('Clear the search').click();
      await settle();
      expect(searchSpy.calls.mostRecent().args[1].search).toBe('');
    });

    it('offers a retry when the library cannot be loaded', async () => {
      searchSpy.and.returnValue(of<DamAssetSearchOutcome>({ status: 'unavailable' }));
      host.open.set(false);
      await settle();
      host.open.set(true);
      await settle();

      expect(root().querySelector('[role="alert"]')?.textContent).toContain('The library could not be loaded.');

      searchSpy.and.callFake(() => found());
      button('Try again').click();
      await settle();

      expect(text()).toContain('Soda bread hero');
    });

    it('loads more with the cursor, appends, and keeps what is shown when a further page fails', async () => {
      searchSpy.and.callFake((_slug, query) =>
        query.cursor === null ? found([assetSummary()], 'c2', 3) : found([assetSummary({ id: 'a2', title: 'Crumb close-up' })], 'c3', null),
      );
      host.open.set(false);
      await settle();
      host.open.set(true);
      await settle();

      button('Load more').click();
      await settle();

      expect(searchSpy.calls.mostRecent().args[1].cursor).toBe('c2');
      expect(text()).toContain('Crumb close-up');
      expect(text()).toContain('2 of 3 pictures shown.');

      searchSpy.and.returnValue(of<DamAssetSearchOutcome>({ status: 'unavailable' }));
      button('Load more').click();
      await settle();

      expect(text()).toContain('More pictures could not be loaded.');
      expect(text()).toContain('Soda bread hero');
    });

    it('starts from a fresh library every time it is opened', async () => {
      await choose();
      host.open.set(false);
      await settle();
      host.open.set(true);
      await settle();

      expect(text()).toContain('Add a picture from the library');
      expect(searchSpy).toHaveBeenCalledTimes(2);
    });

    it('closes on Cancel without asking, since nothing has been entered', async () => {
      button('Cancel').click();
      await settle();

      expect(confirmSpy).not.toHaveBeenCalled();
      expect(host.closes).toBe(1);
    });
  });

  describe('describing the use', () => {
    it('moves to the second step, reads the versions, and puts focus on its heading', async () => {
      await choose();

      expect(text()).toContain('How is this picture used?');
      expect(detailSpy).toHaveBeenCalledOnceWith('cozy-fall', 'a1');
      const focused = document.activeElement as HTMLElement | null;
      expect(focused?.tagName).toBe('H3');
      expect(focused?.textContent).toContain('Soda bread hero');
      expect(focused?.classList.contains('cp-sr-only')).toBeFalse();
    });

    it('offers every role as one radio group, with the lead picture chosen first', async () => {
      await choose();

      const group = root().querySelector('[role="radiogroup"][aria-label="What is it for?"]')!;
      const labels = Array.from(group.querySelectorAll('.label')).map((each) => each.textContent?.trim());

      expect(labels).toEqual(['Lead picture', 'Step picture', 'Gallery', 'In progress', 'Social']);
      expect(radio('role-Hero').checked).toBeTrue();
    });

    it('disables the lead picture, says why, and starts on Gallery when the recipe already has one', async () => {
      host.heroTaken.set(true);
      await settle();
      await choose();

      expect(radio('role-Hero').disabled).toBeTrue();
      expect(text()).toContain('This recipe already has a lead picture.');
      expect(radio('role-Gallery').checked).toBeTrue();
    });

    it('disables the step picture when the recipe has no saved steps', async () => {
      host.steps.set([]);
      await settle();
      await choose();

      expect(radio('role-Step').disabled).toBeTrue();
      expect(text()).toContain('This recipe has no saved steps yet.');
    });

    it('shows the step picker only for a step picture, and requires a step', async () => {
      await choose();
      expect(root().querySelector('#cp-recipe-media-step')).toBeNull();

      await pick('role-Step');
      const select = root().querySelector<HTMLSelectElement>('#cp-recipe-media-step')!;
      expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual([
        'Choose a step',
        'Step 1: Cream the butter and sugar.',
        'Step 2: Fold in the flour.',
      ]);
      expect(select.getAttribute('aria-required')).toBe('true');

      await submit();

      expect(linkSpy).not.toHaveBeenCalled();
      expect(text()).toContain('Choose the step this picture shows.');
      expect(document.activeElement).toBe(root().querySelector('#cp-recipe-media-step'));
    });

    it('links a step picture to the chosen step', async () => {
      await choose();
      await pick('role-Step');

      const select = root().querySelector<HTMLSelectElement>('#cp-recipe-media-step')!;
      select.value = 's2';
      select.dispatchEvent(new Event('change'));
      await settle();
      await submit();

      expect(linkSpy.calls.mostRecent().args[2]).toEqual(
        jasmine.objectContaining({ role: 'Step', instructionStepId: 's2', versionNumber: null }),
      );
    });

    it('follows the current version unless asked to keep one, and then offers every version', async () => {
      await choose();

      expect(radio('pin-follow').checked).toBeTrue();
      expect(root().querySelector('#cp-recipe-media-version')).toBeNull();

      await pick('pin-keep');

      const select = root().querySelector<HTMLSelectElement>('#cp-recipe-media-version')!;
      expect(Array.from(select.options).map((option) => option.textContent?.trim())).toEqual([
        'Version 3 (current)',
        'Version 2',
        'Version 1',
      ]);

      select.value = '1';
      select.dispatchEvent(new Event('change'));
      await settle();

      // The preview is of the version being kept, read by its own number.
      expect(versionContentSpy).toHaveBeenCalledWith('cozy-fall', 'a1', 1);

      await submit();
      expect(linkSpy.calls.mostRecent().args[2].versionNumber).toBe(1);
    });

    it('says so when the versions cannot be read, and offers a retry', async () => {
      detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'unavailable' }));
      await choose();
      await pick('pin-keep');

      expect(text()).toContain('This picture’s versions could not be loaded.');

      detailSpy.and.callFake(() => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({}, 2) }));
      button('Try again').click();
      await settle();

      expect(root().querySelector('#cp-recipe-media-version')).not.toBeNull();
    });

    it('sends the recipe’s token, the caption and one idempotency key, then hands the recipe up', async () => {
      await choose();

      const caption = root().querySelector<HTMLTextAreaElement>('#cp-recipe-media-caption')!;
      caption.value = 'Fresh from the oven.';
      caption.dispatchEvent(new Event('input'));
      await settle();
      await submit();

      const [slug, recipeId, request, key] = linkSpy.calls.mostRecent().args;
      expect(slug).toBe('cozy-fall');
      expect(recipeId).toBe('r1');
      expect(request).toEqual({
        mediaAssetId: 'a1',
        role: 'Hero',
        instructionStepId: null,
        versionNumber: null,
        caption: 'Fresh from the oven.',
        expectedConcurrencyToken: 'token-1',
      });
      expect(key).toMatch(/^[0-9a-f-]{36}$/);
      expect(host.linked.length).toBe(1);
    });

    it('submits on Enter in a field, through the form', async () => {
      await choose();

      root().querySelector<HTMLFormElement>('form.describe')!.dispatchEvent(new Event('submit'));
      await settle();

      expect(linkSpy).toHaveBeenCalledTimes(1);
    });

    it('goes back to the grid with the search kept and focus on the card that was chosen', async () => {
      await choose();
      button('Back').click();
      await settle();

      expect(text()).toContain('Add a picture from the library');
      expect(searchSpy).toHaveBeenCalledTimes(1);
      expect(document.activeElement).toBe(button('Choose Soda bread hero'));
    });

    it('asks before closing once a caption has been written, and stays when told to', async () => {
      await choose();

      const caption = root().querySelector<HTMLTextAreaElement>('#cp-recipe-media-caption')!;
      caption.value = 'Fresh from the oven.';
      caption.dispatchEvent(new Event('input'));
      await settle();

      confirmSpy.and.resolveTo(false);
      root().querySelector<HTMLButtonElement>('button[aria-label="Close dialog"]')!.click();
      await settle();

      expect(confirmSpy).toHaveBeenCalledTimes(1);
      expect(host.closes).toBe(0);

      confirmSpy.and.resolveTo(true);
      root().querySelector<HTMLButtonElement>('button[aria-label="Close dialog"]')!.click();
      await settle();

      expect(host.closes).toBe(1);
    });
  });

  describe('refusals', () => {
    async function refusedWith(outcome: LinkRecipeAssetOutcome): Promise<void> {
      linkSpy.and.resolveTo(outcome);
      await choose();
      await submit();
    }

    function alertText(): string {
      return (root().querySelector('[role="alert"]')?.textContent ?? '').replace(/\s+/g, ' ');
    }

    const worded: readonly (readonly [LinkRecipeAssetOutcome['status'], string])[] = [
      ['hero_exists', 'This recipe already has a lead picture. Unlink it before choosing another.'],
      ['already_linked', 'That picture is already linked to this recipe in that role.'],
      ['archived_conflict', 'This recipe is archived.'],
      ['forbidden', 'You don’t have permission to change this recipe’s pictures.'],
      ['not_found', 'This recipe could not be found.'],
      ['unavailable', 'The picture could not be linked. Check your connection and try again.'],
    ];

    for (const [status, words] of worded) {
      it(`says what happened for ${status}, keeps the dialog open, and moves focus to the message`, async () => {
        await refusedWith({ status } as LinkRecipeAssetOutcome);

        expect(alertText()).toContain(words);
        expect(host.linked.length).toBe(0);
        expect(host.closes).toBe(0);
        expect(radio('role-Hero').checked).toBeTrue();
        expect((document.activeElement as HTMLElement | null)?.hasAttribute('data-problem')).toBeTrue();
      });
    }

    it('offers a reload when the recipe has moved on, and asks the editor for it', async () => {
      await refusedWith({ status: 'conflict' });

      expect(alertText()).toContain('This recipe has changed since you opened it, so nothing was linked.');

      button('Reload the recipe').click();
      await settle();
      expect(host.reloads).toBe(1);
    });

    it('says a refusal about the picture at the top in the server’s words, with the way back', async () => {
      await refusedWith({
        status: 'target_refused',
        fieldErrors: { mediaAssetId: ["That picture is not in this workspace's library."] },
      });

      expect(alertText()).toContain("That picture is not in this workspace's library. Go back and choose another.");
    });

    it('puts a refusal about the version or the step beside its own control', async () => {
      linkSpy.and.resolveTo({
        status: 'target_refused',
        fieldErrors: { versionNumber: ['That picture has no version with that number.'] },
      });
      await choose();
      await pick('pin-keep');
      await submit();

      const select = root().querySelector<HTMLSelectElement>('#cp-recipe-media-version')!;
      const described = (select.getAttribute('aria-describedby') ?? '')
        .split(' ')
        .filter(Boolean)
        .map((id) => root().querySelector(`[id="${id}"]`)?.textContent ?? '')
        .join(' ');

      expect(described).toContain('That picture has no version with that number.');
      expect(document.activeElement).toBe(select);
    });

    it('repeats an unanswered request with the same key, and takes a new one once the request changes', async () => {
      linkSpy.and.resolveTo({ status: 'unavailable' });
      await choose();
      await submit();
      await submit();

      const [first, second] = linkSpy.calls.allArgs().map((args) => args[3]);
      expect(second).toBe(first);

      await pick('role-Gallery');
      await submit();

      expect(linkSpy.calls.mostRecent().args[3]).not.toBe(first);
    });

    it('takes a new key after a definite refusal, so the next press is a new attempt', async () => {
      linkSpy.and.resolveTo({ status: 'already_linked' });
      await choose();
      await submit();
      await submit();

      const [first, second] = linkSpy.calls.allArgs().map((args) => args[3]);
      expect(second).not.toBe(first);
    });

    it('does not send anything while the recipe’s token is not known', async () => {
      host.token.set(null);
      await settle();
      await choose();
      await submit();

      expect(linkSpy).not.toHaveBeenCalled();
      expect(alertText()).toContain('This recipe is still loading.');
    });
  });

  describe('accessibility', () => {
    it('is a labelled modal dialog whose title names the step it is on', async () => {
      const dialog = root().querySelector('[role="dialog"]')!;
      const title = (): string => root().querySelector(`[id="${dialog.getAttribute('aria-labelledby')}"]`)?.textContent ?? '';

      expect(dialog.getAttribute('aria-modal')).toBe('true');
      expect(title()).toBe('Add a picture from the library');

      await choose();
      expect(title()).toBe('How is this picture used?');
    });

    it('labels the search, the step, the version and the caption', async () => {
      const labelOf = (id: string): string => root().querySelector(`label[for="${id}"]`)?.textContent?.trim() ?? '';

      expect(labelOf('cp-dam-picker-search')).toContain('Search the library');

      await choose();
      await pick('role-Step');
      await pick('pin-keep');

      expect(labelOf('cp-recipe-media-step')).toContain('Which step?');
      expect(labelOf('cp-recipe-media-version')).toContain('Version to keep');
      expect(labelOf('cp-recipe-media-caption')).toContain('Caption');
    });

    it('closes on Escape', async () => {
      root().querySelector('cp-dialog')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      await settle();

      expect(host.closes).toBe(1);
    });
  });
});
