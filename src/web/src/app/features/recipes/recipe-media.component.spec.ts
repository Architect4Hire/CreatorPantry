import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { VisibilityService } from '../../core/visibility.service';
import { RecipeMediaStep, UnlinkRecipeAssetOutcome } from '../../models/recipe-asset-link.models';
import { RecipeAssetLink } from '../../models/recipe.models';
import {
  DamAssetContentOutcome,
  DamAssetDetailOutcome,
  DamAssetSearchOutcome,
  DamAssetService,
} from '../../services/dam-asset.service';
import { RecipeAssetLinkService } from '../../services/recipe-asset-link.service';
import { RecipeMediaChanged, RecipeMediaComponent } from './recipe-media.component';
import { MEDIA_STEPS, assetDetail, assetSummary, mediaLink, mediaRecipe } from './recipe-media.testing';

@Component({
  standalone: true,
  imports: [RecipeMediaComponent],
  template: `
    <cp-recipe-media
      workspaceSlug="cozy-fall"
      recipeId="r1"
      [links]="links()"
      [steps]="steps()"
      [concurrencyToken]="token()"
      [editorIsDirty]="dirty()"
      [canChange]="canChange()"
      [isArchived]="archived()"
      (changed)="changes.push($event)"
      (reloadRequested)="reloads = reloads + 1"
    />
  `,
})
class HostComponent {
  readonly links = signal<readonly RecipeAssetLink[]>([mediaLink()]);
  readonly steps = signal<readonly RecipeMediaStep[]>(MEDIA_STEPS);
  readonly token = signal<string | null>('token-1');
  readonly dirty = signal(false);
  readonly canChange = signal(true);
  readonly archived = signal(false);
  readonly changes: RecipeMediaChanged[] = [];
  reloads = 0;
}

const delay = (ms = 0): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('RecipeMediaComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let detailSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetDetailOutcome>>;
  let contentSpy: jasmine.Spy<(slug: string, id: string, rendition?: string) => Observable<DamAssetContentOutcome>>;
  let versionContentSpy: jasmine.Spy<(slug: string, id: string, version: number, rendition?: string) => Observable<DamAssetContentOutcome>>;
  let unlinkSpy: jasmine.Spy<
    (slug: string, recipeId: string, linkId: string, token: string, key: string) => Promise<UnlinkRecipeAssetOutcome>
  >;
  let linkSpy: jasmine.Spy;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return (root().textContent ?? '').replace(/\s+/g, ' ');
  }

  function buttons(label: string): HTMLButtonElement[] {
    return Array.from(root().querySelectorAll('button')).filter(
      (each) => each.textContent?.trim().startsWith(label) || each.getAttribute('aria-label')?.startsWith(label),
    );
  }

  function button(label: string): HTMLButtonElement {
    const [match] = buttons(label);
    if (!match) throw new Error(`no button "${label}"`);
    return match;
  }

  function row(linkId: string): HTMLElement {
    const found = root().querySelector<HTMLElement>(`[data-link-id="${linkId}"]`);
    if (!found) throw new Error(`no row "${linkId}"`);
    return found;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await delay();
    fixture.detectChanges();
  }

  async function create(configure: (host: HostComponent) => void = () => undefined): Promise<void> {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    configure(host);
    await settle();
  }

  beforeEach(async () => {
    detailSpy = jasmine
      .createSpy('detail')
      .and.callFake((_slug, id) => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id }, 3) }));
    contentSpy = jasmine.createSpy('content').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));
    versionContentSpy = jasmine.createSpy('versionContent').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));
    unlinkSpy = jasmine
      .createSpy('unlink')
      .and.resolveTo({ status: 'unlinked', recipe: mediaRecipe({ assetLinks: [] }), replayed: false });
    linkSpy = jasmine.createSpy('link').and.resolveTo({ status: 'linked', recipe: mediaRecipe(), replayed: false });
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        {
          provide: DamAssetService,
          useValue: {
            detail: detailSpy,
            content: contentSpy,
            versionContent: versionContentSpy,
            search: () =>
              of<DamAssetSearchOutcome>({ status: 'found', page: { items: [assetSummary()], nextCursor: null, totalCount: 1 } }),
          },
        },
        { provide: RecipeAssetLinkService, useValue: { unlink: unlinkSpy, link: linkSpy } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();
  });

  describe('what it shows', () => {
    it('says there are no pictures yet, and still offers to add one', async () => {
      await create((h) => h.links.set([]));

      expect(text()).toContain('No pictures linked yet');
      expect(button('Add from library').disabled).toBeFalse();
      expect(detailSpy).not.toHaveBeenCalled();
    });

    it('groups the links under their roles, and names each picture as a link to its library page', async () => {
      await create((h) =>
        h.links.set([
          mediaLink({ id: 'social', role: 'Social', sortOrder: 2, mediaAssetId: 'a3' }),
          mediaLink({ id: 'hero', role: 'Hero', sortOrder: 0, mediaAssetId: 'a1', caption: 'Fresh from the oven.' }),
          mediaLink({ id: 'step', role: 'Step', sortOrder: 1, mediaAssetId: 'a2', instructionStepId: 's2' }),
        ]),
      );

      expect(Array.from(root().querySelectorAll('h3')).map((each) => each.textContent?.trim())).toEqual([
        'Lead picture',
        'Step pictures',
        'Social',
      ]);

      const hero = row('hero');
      expect(hero.querySelector('a')?.getAttribute('href')).toBe('/cozy-fall/dam/a1');
      expect(hero.querySelector('a')?.textContent).toContain('Soda bread hero');
      expect(hero.textContent).toContain('Fresh from the oven.');
      expect(row('step').textContent).toContain('Step 2: Fold in the flour.');
    });

    it('reads the picture for each row once, and only the new row when a link is added', async () => {
      await create((h) =>
        h.links.set([mediaLink({ id: 'l1', role: 'Hero' }), mediaLink({ id: 'l2', role: 'Social', sortOrder: 1, mediaAssetId: 'a2' })]),
      );

      expect(detailSpy.calls.allArgs().map((args) => args[1])).toEqual(['a1', 'a2']);

      // The rows already drawn are kept; a link added later asks only about its own picture.
      host.links.update((links) => [...links, mediaLink({ id: 'l3', role: 'Gallery', sortOrder: 2, mediaAssetId: 'a9' })]);
      await settle();

      expect(detailSpy.calls.allArgs().map((args) => args[1])).toEqual(['a1', 'a2', 'a9']);
    });

    it('says whether a link follows the current version or is kept at one, and shows the kept version', async () => {
      await create((h) =>
        h.links.set([
          mediaLink({ id: 'follows', role: 'Hero' }),
          mediaLink({ id: 'kept', role: 'Gallery', sortOrder: 1, mediaAssetVersionNumber: 1 }),
        ]),
      );

      expect(row('follows').textContent).toContain('Follows the current version (version 3)');
      expect(row('kept').textContent).toContain('Kept at version 1 · version 3 is newer');

      expect(contentSpy).toHaveBeenCalledWith('cozy-fall', 'a1', 'thumbnail');
      expect(versionContentSpy).toHaveBeenCalledWith('cozy-fall', 'a1', 1, 'thumbnail');
    });

    it('keeps the row of a picture no longer in the library, says so, and still lets it be unlinked', async () => {
      detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'not_found' }));
      await create();

      expect(row('l1').textContent).toContain('This picture is no longer in the library.');
      expect(row('l1').querySelector('a')).toBeNull();
      expect(button('Unlink this picture').disabled).toBeFalse();
    });

    it('offers a retry when a picture’s details cannot be loaded', async () => {
      detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'unavailable' }));
      await create();

      expect(row('l1').textContent).toContain('This picture’s details could not be loaded.');

      detailSpy.and.callFake((_slug, id) => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id }) }));
      button('Try again').click();
      await settle();

      expect(row('l1').querySelector('a')?.textContent).toContain('Soda bread hero');
    });

    it('offers nothing that would change the library itself', async () => {
      await create();

      const labels = Array.from(root().querySelectorAll('button')).map((each) =>
        `${each.textContent ?? ''} ${each.getAttribute('aria-label') ?? ''}`.toLowerCase(),
      );

      expect(labels.some((label) => /upload|edit|delete|remove/.test(label))).toBeFalse();
      expect(root().querySelector('input[type="file"]')).toBeNull();
    });
  });

  describe('who may change what', () => {
    it('shows a viewer the pictures and no action, and says why', async () => {
      await create((h) => h.canChange.set(false));

      expect(row('l1').textContent).toContain('Soda bread hero');
      expect(buttons('Add from library')).toEqual([]);
      expect(buttons('Unlink')).toEqual([]);
      expect(text()).toContain('You have view-only access to this workspace');
      expect(root().querySelector('cp-recipe-media-link-dialog')).toBeNull();
    });

    it('shows an archived recipe’s pictures and no action, and says why', async () => {
      await create((h) => h.archived.set(true));

      expect(buttons('Add from library')).toEqual([]);
      expect(buttons('Unlink')).toEqual([]);
      expect(text()).toContain('This recipe is archived, so its pictures can’t be changed.');
    });

    it('disables both actions while the form has unsaved edits, and ties each to the reason', async () => {
      await create((h) => h.dirty.set(true));

      const add = button('Add from library');
      const unlink = button('Unlink Soda bread hero');
      const reason = root().querySelector(`[id="${add.getAttribute('aria-describedby')}"]`);

      expect(add.disabled).toBeTrue();
      expect(unlink.disabled).toBeTrue();
      expect(reason?.textContent).toContain('Save or discard your changes before changing pictures.');
      expect(unlink.getAttribute('aria-describedby')).toBe(add.getAttribute('aria-describedby'));

      host.dirty.set(false);
      await settle();

      expect(button('Add from library').disabled).toBeFalse();
      expect(button('Add from library').hasAttribute('aria-describedby')).toBeFalse();
    });
  });

  describe('linking', () => {
    it('opens the library dialog, told whether the lead picture is taken', async () => {
      await create((h) => h.links.set([mediaLink({ role: 'Hero' })]));

      expect(root().querySelector('[role="dialog"]')).toBeNull();

      button('Add from library').click();
      await settle();

      expect(root().querySelector('[role="dialog"]')).not.toBeNull();

      button('Choose Soda bread hero').click();
      await settle();

      expect(root().querySelector<HTMLInputElement>('input[id$="role-Hero"]')?.disabled).toBeTrue();
    });

    it('hands the linked recipe up, closes the dialog, and returns focus to the button that opened it', async () => {
      await create();

      button('Add from library').click();
      await settle();
      button('Choose Soda bread hero').click();
      await settle();
      button('Link picture').click();
      await settle();

      expect(linkSpy.calls.mostRecent().args[2].expectedConcurrencyToken).toBe('token-1');
      expect(host.changes.map((change) => change.message)).toEqual(['Picture linked.']);
      expect(root().querySelector('[role="dialog"]')).toBeNull();
      expect(document.activeElement).toBe(button('Add from library'));
    });

    it('returns focus to the button when the dialog is closed without linking', async () => {
      await create();

      button('Add from library').click();
      await settle();
      button('Cancel').click();
      await settle();

      expect(host.changes).toEqual([]);
      expect(document.activeElement).toBe(button('Add from library'));
    });
  });

  describe('unlinking', () => {
    it('asks first, in words that say the picture stays in the library, and never says delete', async () => {
      await create();

      button('Unlink Soda bread hero').click();
      await settle();

      const question = confirmSpy.calls.mostRecent().args[0];
      const words = `${question.title} ${question.message} ${question.confirmLabel} ${question.cancelLabel}`;

      expect(question.title).toBe('Unlink this picture from the recipe?');
      expect(question.message).toContain('stays in your library with its versions and usage history');
      expect(question.confirmLabel).toBe('Unlink');
      expect(words.toLowerCase()).not.toMatch(/delete|remove/);
    });

    it('does nothing when the creator keeps the link', async () => {
      confirmSpy.and.resolveTo(false);
      await create();

      button('Unlink Soda bread hero').click();
      await settle();

      expect(unlinkSpy).not.toHaveBeenCalled();
      expect(button('Unlink Soda bread hero').disabled).toBeFalse();
    });

    it('unlinks with the recipe’s token and a key, hands the recipe up, and moves focus to Add', async () => {
      await create();

      button('Unlink Soda bread hero').click();
      await settle();

      const [slug, recipeId, linkId, token, key] = unlinkSpy.calls.mostRecent().args;
      expect([slug, recipeId, linkId, token]).toEqual(['cozy-fall', 'r1', 'l1', 'token-1']);
      expect(key).toMatch(/^[0-9a-f-]{36}$/);

      expect(host.changes.length).toBe(1);
      expect(host.changes[0].message).toBe('Picture unlinked. It is still in your library.');
      expect(host.changes[0].recipe.assetLinks).toEqual([]);
      expect(document.activeElement).toBe(button('Add from library'));
    });

    it('holds every action while one unlink is being decided', async () => {
      let answer: (value: boolean) => void = () => undefined;
      confirmSpy.and.returnValue(new Promise<boolean>((resolve) => (answer = resolve)));
      await create((h) => h.links.set([mediaLink({ id: 'l1', role: 'Hero' }), mediaLink({ id: 'l2', sortOrder: 1, mediaAssetId: 'a2' })]));

      buttons('Unlink')[0].click();
      await settle();

      expect(buttons('Unlink').every((each) => each.disabled)).toBeTrue();
      expect(button('Add from library').disabled).toBeTrue();

      answer(false);
      await settle();

      expect(buttons('Unlink').every((each) => !each.disabled)).toBeTrue();
    });

    const worded: readonly (readonly [UnlinkRecipeAssetOutcome['status'], string, boolean])[] = [
      ['conflict', 'This recipe has changed since you opened it, so nothing was unlinked.', true],
      ['link_not_found', 'That picture is no longer linked to this recipe.', true],
      ['archived_conflict', 'This recipe is archived.', false],
      ['forbidden', 'You don’t have permission to change this recipe’s pictures.', false],
      ['not_found', 'This recipe could not be found.', false],
      ['unavailable', 'The picture could not be unlinked. Check your connection and try again.', false],
    ];

    for (const [status, words, offersReload] of worded) {
      it(`says what happened for ${status}, as an alert that takes focus`, async () => {
        unlinkSpy.and.resolveTo({ status } as UnlinkRecipeAssetOutcome);
        await create();

        button('Unlink Soda bread hero').click();
        await settle();

        expect(root().querySelector('[role="alert"]')?.textContent).toContain(words);
        expect((document.activeElement as HTMLElement | null)?.hasAttribute('data-problem')).toBeTrue();
        expect(host.changes).toEqual([]);
        expect(buttons('Reload the recipe').length).toBe(offersReload ? 1 : 0);
      });
    }

    it('asks the editor to reload when that is the remedy', async () => {
      unlinkSpy.and.resolveTo({ status: 'conflict' });
      await create();

      button('Unlink Soda bread hero').click();
      await settle();
      button('Reload the recipe').click();

      expect(host.reloads).toBe(1);
    });

    it('repeats an unanswered unlink with the same key, and takes a new one for a new token', async () => {
      unlinkSpy.and.resolveTo({ status: 'unavailable' });
      await create();

      button('Unlink Soda bread hero').click();
      await settle();
      button('Unlink Soda bread hero').click();
      await settle();

      const [first, second] = unlinkSpy.calls.allArgs().map((args) => args[4]);
      expect(second).toBe(first);

      host.token.set('token-2');
      await settle();
      button('Unlink Soda bread hero').click();
      await settle();

      expect(unlinkSpy.calls.mostRecent().args[4]).not.toBe(first);
    });
  });

  describe('accessibility', () => {
    it('is a named region with a heading per group, and names every Unlink by its picture', async () => {
      await create((h) =>
        h.links.set([mediaLink({ id: 'l1', role: 'Hero' }), mediaLink({ id: 'l2', role: 'Gallery', sortOrder: 1, mediaAssetId: 'a2' })]),
      );

      const region = root().querySelector('section.media')!;
      expect(root().querySelector(`[id="${region.getAttribute('aria-labelledby')}"]`)?.textContent).toContain('Pictures');

      for (const group of Array.from(root().querySelectorAll('section.group'))) {
        expect(root().querySelector(`[id="${group.getAttribute('aria-labelledby')}"]`)).not.toBeNull();
      }

      expect(buttons('Unlink').map((each) => each.getAttribute('aria-label'))).toEqual([
        'Unlink Soda bread hero',
        'Unlink Soda bread hero',
      ]);
      expect(buttons('Unlink').every((each) => each.textContent?.trim() === 'Unlink')).toBeTrue();
    });
  });
});
