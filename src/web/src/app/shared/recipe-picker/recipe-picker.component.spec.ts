import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { CreativeContextSession } from '../../services/creative-context-session';
import { FakeCreativeContextService } from '../../services/creative-context.fake';
import { CreativeContextService } from '../../services/creative-context.service';
import { FakeRecipeLibrary } from '../../services/recipe-library.fake';
import { RecipeService } from '../../services/recipe.service';
import { RecipePickerComponent } from './recipe-picker.component';

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const CTX = 'ctx-1';
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

@Component({
  imports: [RecipePickerComponent],
  providers: [CreativeContextSession],
  template: `<cp-recipe-picker
    [workspaceSlug]="slug()"
    [session]="session"
    (announced)="announcements.push($event)"
    (linkedRecipe)="named.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal(SLUG);
  readonly announcements: string[] = [];
  readonly named: ({ readonly id: string; readonly title: string } | null)[] = [];

  constructor(readonly session: CreativeContextSession) {}
}

describe('RecipePickerComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let el: HTMLElement;
  let server: FakeCreativeContextService;
  let library: FakeRecipeLibrary;

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      fixture.detectChanges();
      await delay(0);
    }
    fixture.detectChanges();
  }

  /** Mount on a context that already exists, as a surface opened for one would be. */
  async function mount(options: { slug?: string; linked?: { recipeId: string; recipeVersionId: string | null } } = {}): Promise<void> {
    const slug = options.slug ?? SLUG;
    server.seed(slug, CTX, {
      references: options.linked
        ? [server.reference({ kind: 'Recipe', recipeId: options.linked.recipeId, recipeVersionId: options.linked.recipeVersionId }, 0)]
        : [],
    });

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.slug.set(slug);
    el = fixture.nativeElement;
    await host.session.load(slug, CTX);
    await settle();
  }

  function box(): HTMLInputElement {
    return el.querySelector<HTMLInputElement>('input[role="combobox"]')!;
  }

  async function type(value: string): Promise<void> {
    box().focus();
    box().value = value;
    box().dispatchEvent(new Event('input'));
    // The text reaches the search on the next render, and the search then waits for the typing to rest.
    await settle();
    await delay(300);
    await settle();
  }

  function optionLabels(): string[] {
    return Array.from(el.querySelectorAll('[role="option"]')).map((option) => option.textContent?.trim() ?? '');
  }

  async function pick(label: string): Promise<void> {
    const option = Array.from(el.querySelectorAll<HTMLElement>('[role="option"]')).find((each) =>
      each.textContent?.includes(label),
    )!;
    option.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    option.click();
    await settle();
  }

  function button(label: string): HTMLButtonElement | null {
    return (
      Array.from(el.querySelectorAll('button')).find((each) => each.textContent?.trim() === label) ?? null
    );
  }

  async function click(label: string): Promise<void> {
    button(label)!.click();
    await settle();
  }

  function references() {
    return server.stored(host.slug(), CTX)!.references;
  }

  beforeEach(() => {
    server = new FakeCreativeContextService();
    library = new FakeRecipeLibrary([
      { slug: SLUG, id: 'r-soda', title: 'Soda bread', versionIds: ['v-soda-1', 'v-soda-2'] },
      { slug: SLUG, id: 'r-chili', title: 'Weeknight chili', status: 'Approved', versionIds: ['v-chili-1'] },
      { slug: SLUG, id: 'r-new', title: 'Something new', versionIds: [] },
      { slug: SLUG, id: 'r-old', title: 'Retired soda farl', status: 'Archived', versionIds: ['v-old-1'] },
      { slug: OTHER_SLUG, id: 'r-theirs', title: 'Their soda bread', versionIds: ['v-theirs-1'] },
    ]);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CreativeContextService, useValue: server },
        { provide: RecipeService, useValue: library },
      ],
    });
  });

  describe('searching', () => {
    it('names its search box, and says the link is optional', async () => {
      await mount();

      expect(box().getAttribute('aria-labelledby') ?? el.querySelector(`label[for="${box().id}"]`)?.textContent).toBeTruthy();
      expect(el.textContent).toContain('Find a recipe');
      expect(el.textContent).toContain('Optional');
    });

    it('finds recipes by part of their name', async () => {
      await mount();

      await type('soda');

      expect(optionLabels().some((label) => label.includes('Soda bread'))).toBeTrue();
      expect(optionLabels().some((label) => label.includes('Weeknight chili'))).toBeFalse();
    });

    it('never offers an archived recipe', async () => {
      await mount();

      await type('soda');

      expect(el.textContent).not.toContain('Retired soda farl');
      expect(library.searches.every((search) => !search.query.statuses.includes('Archived'))).toBeTrue();
    });

    it("never offers another workspace's recipe", async () => {
      await mount();

      await type('soda');

      expect(el.textContent).not.toContain('Their soda bread');
      expect(library.searches.every((search) => search.slug === SLUG)).toBeTrue();
    });

    it('says so when nothing matches', async () => {
      await mount();

      await type('zzz');

      expect(optionLabels()).toEqual([]);
      expect(el.textContent).toContain('No recipes match that.');
    });

    it('says the library is empty, with the way to it, rather than offering a search of nothing', async () => {
      library = new FakeRecipeLibrary([]);
      TestBed.overrideProvider(RecipeService, { useValue: library });
      await mount();

      expect(el.textContent).toContain('You have no recipes yet');
      expect(el.querySelector('a')?.getAttribute('href')).toBe(`/${SLUG}/recipes`);
      expect(el.querySelector('input[role="combobox"]')).toBeNull();
    });

    it('says the search failed, keeps the box, and searches again on a retry', async () => {
      library.offline = true;
      await mount();

      expect(el.textContent).toContain("couldn't be searched right now");
      expect(el.querySelector('[role="alert"]')).not.toBeNull();

      library.offline = false;
      await click('Try again');
      await type('chili');

      expect(optionLabels().some((label) => label.includes('Weeknight chili'))).toBeTrue();
    });
  });

  describe('linking', () => {
    it('stores the recipe on the context, pinned to the version that is current now', async () => {
      await mount();
      await type('soda');

      await pick('Soda bread');

      expect(references().map((each) => [each.kind, each.recipeId, each.recipeVersionId])).toEqual([
        ['Recipe', 'r-soda', 'v-soda-2'],
      ]);
      expect(host.announcements).toContain('Soda bread is linked.');
    });

    it('shows what is linked as a summary, with its version and a way to the recipe', async () => {
      await mount();
      await type('soda');
      await pick('Soda bread');

      expect(el.textContent).toContain('Soda bread');
      expect(el.textContent).toContain('Version 2, as it was when you linked it.');
      expect(el.querySelector('a')?.getAttribute('href')).toBe(`/${SLUG}/recipes/r-soda`);
      expect(el.querySelector('input[role="combobox"]')).withContext('one recipe per run').toBeNull();
    });

    it('links a recipe with no saved version without pinning one', async () => {
      await mount();
      await type('new');

      await pick('Something new');

      expect(references()[0].recipeId).toBe('r-new');
      expect(references()[0].recipeVersionId).toBeNull();
      expect(el.textContent).toContain('No saved version yet');
    });

    it('says so, and links nothing, when the recipe was archived between the search and the choice', async () => {
      await mount();
      await type('chili');
      server.unusableRecipeIds.add('r-chili');

      await pick('Weeknight chili');

      expect(references()).toEqual([]);
      expect(el.textContent).toContain('can no longer be linked');
      expect(el.querySelector('[role="alert"]')).not.toBeNull();
    });

    it('links nothing when the server cannot be reached, and links on a retry', async () => {
      await mount();
      await type('soda');
      server.offline = true;

      await pick('Soda bread');

      expect(el.textContent).toContain("couldn't be done right now");

      server.offline = false;
      await click('Try again');

      expect(references().map((each) => each.recipeId)).toEqual(['r-soda']);
    });
  });

  describe('a linked recipe', () => {
    it('is read back from the context when the work is opened', async () => {
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-2' } });

      expect(el.textContent).toContain('Soda bread');
      expect(el.textContent).toContain('Version 2');
      expect(el.textContent).not.toContain('has changed');
    });

    it('is unlinked by Remove, which leaves the recipe itself alone and asks nothing', async () => {
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-2' } });

      await click('Remove');

      expect(references()).toEqual([]);
      expect(host.announcements).toContain('The recipe is no longer linked.');
      expect(el.querySelector('input[role="combobox"]')).not.toBeNull();
    });

    it('names the recipe in the Remove button, so it reads alone', async () => {
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-2' } });

      expect(button('Remove')!.getAttribute('aria-label')).toBe('Remove the link to Soda bread');
    });

    it('says the recipe has changed when it has a newer version, and keeps using the one linked', async () => {
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-1' } });

      expect(el.textContent).toContain('This recipe has changed since you linked it');
      expect(el.textContent).toContain('now at version 2');
      expect(el.textContent).toContain('Version 1, as it was when you linked it.');
      expect(references()[0].recipeVersionId).withContext('never switched silently').toBe('v-soda-1');
      expect(server.added.length).toBe(0);
    });

    it('pins the new version only when the creator asks', async () => {
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-1' } });

      await click('Use the new version');

      expect(references().map((each) => [each.recipeId, each.recipeVersionId])).toEqual([['r-soda', 'v-soda-2']]);
      expect(el.textContent).not.toContain('has changed');
      expect(el.textContent).toContain('Version 2');
    });

    it('says a recipe archived since it was linked is no longer available, and still lets it be removed', async () => {
      await mount({ linked: { recipeId: 'r-old', recipeVersionId: 'v-old-1' } });

      expect(el.textContent).toContain('no longer available');
      expect(el.textContent).not.toContain('Retired soda farl');

      await click('Remove');

      expect(references()).toEqual([]);
    });

    it("reads another workspace's recipe as no longer available, and shows nothing of it", async () => {
      await mount({ linked: { recipeId: 'r-theirs', recipeVersionId: 'v-theirs-1' } });

      expect(el.textContent).toContain('no longer available');
      expect(el.textContent).not.toContain('Their soda bread');
    });

    it('says it is still linked when it could not be read, and reads it on a retry', async () => {
      library.offline = true;
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-2' } });

      expect(el.textContent).toContain("couldn't be read right now. It is still linked.");

      library.offline = false;
      await click('Try again');

      expect(el.textContent).toContain('Soda bread');
    });
  });

  describe('naming the linked recipe for the page (AF.4.2)', () => {
    it('reports the recipe with its title, which the context names only by id', async () => {
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-2' } });

      expect(host.named).toEqual([{ id: 'r-soda', title: 'Soda bread' }]);
    });

    it('says nothing while nothing is linked, then reports a link and its removal', async () => {
      // Silent to begin with: a page holding "no recipe" is already right, and one answer is said once.
      await mount();
      expect(host.named).toEqual([]);

      await type('soda');
      await pick('Soda bread');
      expect(host.named).toEqual([{ id: 'r-soda', title: 'Soda bread' }]);

      await click('Remove');
      expect(host.named).toEqual([{ id: 'r-soda', title: 'Soda bread' }, null]);
    });

    it('names no recipe it could not read, so no page offers a title it is unsure of', async () => {
      library.offline = true;
      await mount({ linked: { recipeId: 'r-soda', recipeVersionId: 'v-soda-2' } });

      expect(host.named).toEqual([]);

      library.offline = false;
      await click('Try again');

      expect(host.named).toEqual([{ id: 'r-soda', title: 'Soda bread' }]);
    });
  });
});
