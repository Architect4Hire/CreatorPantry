import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { PictureRendition } from '../../models/picture-rendition.models';
import { DamAssetContentOutcome, DamAssetDetailOutcome, DamAssetService } from '../../services/dam-asset.service';
import { RecipeHeroPicture, formatKitchenMinutes, recipeFacts, recipeHeroLink } from './recipe-head';
import { assetDetail, mediaLink, mediaRecipe } from './recipe-media.testing';

describe('recipeFacts', () => {
  it('lists what the recipe records, in the order a cook checks it, as stored', () => {
    const facts = recipeFacts(
      mediaRecipe({
        yieldText: 'Two 9-inch loaves',
        servingCount: 12,
        prepTimeMinutes: 20,
        cookTimeMinutes: 45,
        restTimeMinutes: 60,
        // Not the sum of the three above, on purpose: the stored total is what is shown.
        totalTimeMinutes: 240,
      }),
    );

    expect(facts).toEqual([
      { label: 'Makes', value: 'Two 9-inch loaves' },
      { label: 'Serves', value: '12' },
      { label: 'Prep', value: '20 min' },
      { label: 'Cook', value: '45 min' },
      { label: 'Rest', value: '1 hr' },
      { label: 'Total', value: '4 hr' },
    ]);
  });

  it("keeps the creator's yield wording verbatim and the serving count a fact of its own", () => {
    const facts = recipeFacts(mediaRecipe({ yieldText: 'serves 6 (or 4 hungry ones)', servingCount: 6 }));

    expect(facts).toEqual([
      { label: 'Makes', value: 'serves 6 (or 4 hungry ones)' },
      { label: 'Serves', value: '6' },
    ]);
  });

  it('leaves out what the recipe does not record, and never works out a total', () => {
    expect(recipeFacts(mediaRecipe({ prepTimeMinutes: 10, cookTimeMinutes: 30 }))).toEqual([
      { label: 'Prep', value: '10 min' },
      { label: 'Cook', value: '30 min' },
    ]);
    expect(recipeFacts(mediaRecipe())).toEqual([]);
  });

  it('shows a zero it was given, which is a recorded time and not a missing one', () => {
    expect(recipeFacts(mediaRecipe({ restTimeMinutes: 0, servingCount: 0 }))).toEqual([
      { label: 'Serves', value: '0' },
      { label: 'Rest', value: '0 min' },
    ]);
  });

  it('words minutes the way a cook says them', () => {
    expect(formatKitchenMinutes(80)).toBe('1 hr 20 min');
  });
});

describe('recipeHeroLink', () => {
  it('is the first lead picture by its order, whatever else is linked', () => {
    const links = [
      mediaLink({ id: 'gallery', role: 'Gallery', sortOrder: 0 }),
      mediaLink({ id: 'second', role: 'Hero', sortOrder: 2 }),
      mediaLink({ id: 'first', role: 'Hero', sortOrder: 1 }),
    ];

    expect(recipeHeroLink(links)?.id).toBe('first');
  });

  it('is null for a recipe with no lead picture', () => {
    expect(recipeHeroLink([mediaLink({ role: 'Gallery' }), mediaLink({ role: 'Step', instructionStepId: 's1' })])).toBeNull();
  });
});

class StubDamAssetService {
  detailOutcome: DamAssetDetailOutcome = { status: 'found', asset: assetDetail({ id: 'a1', altText: 'A sliced cake on a blue plate' }, 3) };
  contentOutcome: DamAssetContentOutcome = { status: 'found', bytes: new Blob(['picture'], { type: 'image/png' }) };
  readonly calls: string[] = [];

  detail(workspaceSlug: string, assetId: string): Observable<DamAssetDetailOutcome> {
    this.calls.push(`detail ${workspaceSlug} ${assetId}`);
    return of(this.detailOutcome);
  }

  content(workspaceSlug: string, assetId: string, rendition?: PictureRendition): Observable<DamAssetContentOutcome> {
    this.calls.push(`content ${workspaceSlug} ${assetId} ${rendition}`);
    return of(this.contentOutcome);
  }

  versionContent(workspaceSlug: string, assetId: string, version: number, rendition?: PictureRendition): Observable<DamAssetContentOutcome> {
    this.calls.push(`version ${workspaceSlug} ${assetId} ${version} ${rendition}`);
    return of(this.contentOutcome);
  }
}

@Component({ template: '', providers: [RecipeHeroPicture] })
class HostComponent {
  readonly hero = inject(RecipeHeroPicture);
}

describe('RecipeHeroPicture', () => {
  let assets: StubDamAssetService;
  let revoked: string[];

  beforeEach(() => {
    assets = new StubDamAssetService();
    revoked = [];
    spyOn(URL, 'revokeObjectURL').and.callFake((url: string) => revoked.push(url));
    TestBed.configureTestingModule({ providers: [{ provide: DamAssetService, useValue: assets }] });
  });

  function create() {
    const fixture = TestBed.createComponent(HostComponent);
    return { fixture, hero: fixture.componentInstance.hero };
  }

  it('asks for nothing and shows nothing when the recipe has no lead picture', () => {
    const { hero } = create();
    hero.show('cozy-fall', [mediaLink({ role: 'Gallery' })]);

    expect(hero.state()).toEqual({ status: 'none' });
    expect(assets.calls).toEqual([]);
  });

  it("shows the current version's web copy, never the original, with the alt text the creator wrote", () => {
    const { hero } = create();
    hero.show('cozy-fall', [mediaLink({ role: 'Hero' })]);

    expect(assets.calls).toEqual(['detail cozy-fall a1', 'content cozy-fall a1 web']);
    const state = hero.state();
    expect(state.status).toBe('ready');
    if (state.status !== 'ready') return;
    expect(state.url).toMatch(/^blob:/);
    expect(state.alt).toBe('A sliced cake on a blue plate');
  });

  it('names the picture and says it has no description when none was written, and claims nothing about it', () => {
    assets.detailOutcome = { status: 'found', asset: assetDetail({ id: 'a1', title: 'Cake, overhead', altText: null }) };
    const { hero } = create();
    hero.show('cozy-fall', [mediaLink({ role: 'Hero' })]);

    const state = hero.state();
    expect(state.status === 'ready' ? state.alt : null).toBe('Picture titled “Cake, overhead”. No description has been written for it.');
  });

  it('shows the version a link is held at, and the current one by its own route when that is the version held', () => {
    const { hero } = create();
    hero.show('cozy-fall', [mediaLink({ role: 'Hero', mediaAssetVersionNumber: 1 })]);
    expect(assets.calls.at(-1)).toBe('version cozy-fall a1 1 web');

    hero.show('cozy-fall', [mediaLink({ role: 'Hero', mediaAssetVersionNumber: 3 })]);
    expect(assets.calls.at(-1)).toBe('content cozy-fall a1 web');
  });

  it('shows nothing for a picture gone from the library, or bytes that could not be read', () => {
    assets.detailOutcome = { status: 'not_found' };
    const gone = create().hero;
    gone.show('cozy-fall', [mediaLink({ role: 'Hero' })]);
    expect(gone.state()).toEqual({ status: 'none' });
    expect(assets.calls).toEqual(['detail cozy-fall a1']);

    assets.detailOutcome = { status: 'found', asset: assetDetail({ id: 'a1' }) };
    assets.contentOutcome = { status: 'unavailable' };
    const unreadable = create().hero;
    unreadable.show('cozy-fall', [mediaLink({ role: 'Hero' })]);
    expect(unreadable.state()).toEqual({ status: 'none' });
  });

  it('fetches nothing again when the same links come back after a save', () => {
    const { hero } = create();
    hero.show('cozy-fall', [mediaLink({ role: 'Hero' })]);
    hero.show('cozy-fall', [mediaLink({ role: 'Hero' }), mediaLink({ id: 'l2', role: 'Gallery' })]);

    expect(assets.calls.length).toBe(2);
  });

  it('lets go of the picture it made when the lead picture goes, and when the page does', () => {
    const { fixture, hero } = create();
    hero.show('cozy-fall', [mediaLink({ role: 'Hero' })]);
    const first = hero.state();
    hero.show('cozy-fall', []);
    expect(hero.state()).toEqual({ status: 'none' });
    expect(revoked).toEqual([first.status === 'ready' ? first.url : '']);

    hero.show('cozy-fall', [mediaLink({ role: 'Hero', mediaAssetId: 'a2' })]);
    const second = hero.state();
    fixture.destroy();
    expect(revoked.at(-1)).toBe(second.status === 'ready' ? second.url : '');
  });
});
