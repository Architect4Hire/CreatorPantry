import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { NEVER, of } from 'rxjs';

import { BrandProfileService } from '../../services/brand-profile.service';
import { PublishingService } from '../../services/publishing.service';
import { FakeContentBoardService, boardCard } from '../../services/content-board.fake';
import { ContentBoardService } from '../../services/content-board.service';
import { ContentCardDetailComponent } from './content-card-detail.component';

const SLUG = 'cozy-fall';
const CTX = 'ctx-1';

@Component({ template: '<p>elsewhere</p>' })
class StubComponent {}

describe('ContentCardDetailComponent', () => {
  let server: FakeContentBoardService;
  let harness: RouterTestingHarness;
  let confirmAnswer: boolean;
  let asked: ConfirmRequest[];

  const root = (): HTMLElement => harness.routeNativeElement!;
  const text = (): string => (root().textContent ?? '').replace(/\s+/g, ' ');
  /** An open drawer moves itself to <body>, so it is found from there. */
  const drawer = (): HTMLElement | null => document.body.querySelector('[role="dialog"]');
  const topic = (heading: string): HTMLElement =>
    Array.from(root().querySelectorAll('cp-topic-card')).find(
      (each) => each.querySelector('.heading')?.textContent === heading,
    ) as HTMLElement;
  const button = (name: string, within: ParentNode = root()): HTMLButtonElement =>
    Array.from(within.querySelectorAll('button')).find((each) => each.textContent?.trim() === name)!;

  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i += 1) {
      harness.detectChanges();
      await harness.fixture.whenStable();
    }
    harness.detectChanges();
  }

  async function mount(cardId = 'a', slug = SLUG): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: ':workspaceSlug/content-board/:cardId',
            component: ContentCardDetailComponent,
            data: { title: 'Card' },
          },
          { path: '**', component: StubComponent },
        ]),
        { provide: ContentBoardService, useValue: server },
        {
          provide: BrandProfileService,
          useValue: {
            listContentChannels: () =>
              Promise.resolve({
                status: 'found',
                channels: [{ key: 'instagram', displayName: 'Instagram', isActive: true }],
              }),
            getBrandProfile: () => Promise.resolve({ status: 'found', profile: { timeZoneId: 'America/Chicago' } }),
          },
        },
        {
          // The Publishing topic does its own reading; this page's specs are about the page.
          provide: PublishingService,
          useValue: {
            forCard: () => of({ status: 'read', list: { items: [], complete: true } }),
            profiles: () =>
              of({
                status: 'read',
                profiles: {
                  connection: {
                    providerKey: 'buffer',
                    status: 'Connected',
                    lastCheckedAt: null,
                    lastSucceededAt: null,
                    retryAfterSeconds: null,
                  },
                  profiles: [
                    {
                      id: 'prof-1',
                      providerKey: 'buffer',
                      platform: 'Instagram',
                      displayName: 'Cozy Fall Kitchen',
                      timeZoneId: 'America/Chicago',
                      status: 'Ready',
                      lastSeenAt: '2026-10-13T10:00:00Z',
                    },
                  ],
                },
              }),
          },
        },
        {
          provide: ConfirmService,
          useValue: {
            confirm: (request: ConfirmRequest) => {
              asked.push(request);

              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/${slug}/content-board/${cardId}`, ContentCardDetailComponent);
    await settle();
  }

  function type(id: string, value: string): void {
    const control = document.body.querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!;
    control.value = value;
    control.dispatchEvent(new Event('input'));
  }

  async function openEdit(): Promise<void> {
    button('Edit card').click();
    await settle();
  }

  beforeEach(() => {
    server = new FakeContentBoardService();
    server.seed(SLUG, [
      boardCard('a', 'InProcess', {
        title: 'Soda bread',
        descriptionPreview: 'Hero shot.\nThen the three posts.',
        channelKey: 'instagram',
        plannedDate: '2026-10-16',
        plannedDay: 'Friday',
        moveCount: 1,
        lineage: {
          creativeContextId: CTX,
          recipeId: 'recipe-1',
          recipeVersionId: 'version-1',
          promptRecordId: 'prompt-1',
          mediaAssetId: 'asset-1',
          mediaAssetVersionNumber: 2,
          socialPackageId: 'posts-1',
        },
      }),
      boardCard('bare', 'ToDo', { title: 'Just an idea' }),
    ]);
    server.extras.set('a', {
      lineage: [
        {
          kind: 'CreativeContext',
          id: CTX,
          versionId: null,
          versionNumber: null,
          label: 'Soda bread, autumn',
          available: true,
        },
        {
          kind: 'Recipe',
          id: 'recipe-1',
          versionId: 'version-1',
          versionNumber: null,
          label: 'Buttermilk Soda Bread',
          available: true,
        },
        { kind: 'PromptRecord', id: 'prompt-1', versionId: null, versionNumber: null, label: null, available: true },
        { kind: 'MediaAsset', id: 'asset-1', versionId: null, versionNumber: 2, label: null, available: false },
        { kind: 'SocialPackage', id: 'posts-1', versionId: null, versionNumber: null, label: null, available: true },
      ],
      history: [
        { sequence: 1, fromColumn: null, toColumn: 'ToDo', note: null, occurredAt: '2026-10-13T09:00:00Z' },
        {
          sequence: 2,
          fromColumn: 'ToDo',
          toColumn: 'InProcess',
          note: 'Shoot booked for Friday',
          occurredAt: '2026-10-13T10:00:00Z',
        },
      ],
    });
    confirmAnswer = true;
    asked = [];
  });

  afterEach(async () => {
    // Leave no drawer in <body> for the next spec.
    await harness?.navigateByUrl('/elsewhere');
  });

  // ---- The page ----

  it('is a detail page: one head named for the card, a way back, and its state in words', async () => {
    await mount();

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')!.textContent).toBe('Soda bread');
    const back = root().querySelector('a[cpBackLink]')!;
    expect(back.textContent).toContain('Content Board');
    expect(back.getAttribute('href')).toBe(`/${SLUG}/content-board`);

    const head = root().querySelector('cp-page-head')!;
    expect(head.querySelector('cp-status-pill')!.textContent).toContain('Not scheduled');
    expect(head.textContent).toContain('In process');
    expect(head.textContent).toContain('Instagram');
  });

  it('shows the plan in full, keeping the creator’s line breaks, with the date as the day it names', async () => {
    await mount();

    const plan = topic('The plan');
    expect(plan.querySelector('.description')!.textContent).toBe('Hero shot.\nThen the three posts.');
    expect(plan.textContent).toContain('Friday, October 16, 2026');
    expect(plan.textContent).toContain('Instagram');
  });

  it('lists what the card points at as links to each record, and says when one is no longer there', async () => {
    await mount();

    const lineage = topic('What it points at');
    const links = Array.from(lineage.querySelectorAll('a')).map((link) => [
      link.textContent,
      link.getAttribute('href'),
    ]);
    expect(links).toEqual([
      ['Buttermilk Soda Bread', `/${SLUG}/recipes/recipe-1`],
      ['A saved prompt', `/${SLUG}/prompt-library/prompt-1`],
    ]);

    expect(lineage.textContent).toContain('Soda bread, autumn');
    expect(lineage.textContent).toContain('One exact version');
    // The picture has gone from the library: still listed, said to be unavailable, and not a link.
    expect(lineage.textContent).toContain('A picture in your library');
    expect(lineage.textContent).toContain('Version 2');
    expect(lineage.querySelector('cp-status-pill')!.textContent).toContain('No longer available');
    expect(lineage.textContent).toContain('it holds no copy of them');
  });

  it('offers the way to the posts for the card’s piece of work', async () => {
    await mount();

    const link = topic('Posts').querySelector('a')!;
    expect(link.textContent).toBe('Open the posts');
    expect(link.getAttribute('href')).toBe(`/${SLUG}/social-studio/context/${CTX}`);
  });

  it('says there are no posts to open for a card that came from no piece of work', async () => {
    await mount('bare');

    expect(topic('Posts').querySelector('a')).toBeNull();
    expect(topic('Posts').textContent).toContain('was not made from a piece of work');
    expect(topic('What it points at').textContent).toContain('This card points at nothing yet');
    expect(topic('The plan').textContent).toContain('No notes on this card yet.');
  });

  it('says nothing is scheduled for a card with no publications, whatever column it is in', async () => {
    server.touch(SLUG, 'a', { column: 'Approved' });
    await mount();

    // Approved is the creator's judgement of the work; whether anything went out is a publication's to say.
    const publishing = topic('Publishing');
    expect(publishing.textContent).toContain('Nothing has been scheduled for this card');
    expect(root().querySelector('cp-page-head cp-status-pill')!.textContent).toContain('Not scheduled');
  });

  it('offers the one way to confirm a publication, under the topic it belongs to', async () => {
    await mount();

    expect(button('Schedule a publication', topic('Publishing'))).toBeTruthy();
  });

  it('reads the history oldest first, with each move’s note', async () => {
    await mount();

    const entries = Array.from(topic('History').querySelectorAll('ol li')).map((each) =>
      each.textContent!.replace(/\s+/g, ' ').trim(),
    );
    expect(entries.length).toBe(2);
    expect(entries[0]).toContain('Created in To do');
    expect(entries[1]).toContain('Moved from To do to In process');
    expect(entries[1]).toContain('Shoot booked for Friday');
  });

  it('says a card could not be found, for one that is unknown or another workspace’s alike', async () => {
    await mount('nobody-has-this');
    expect(root().querySelector('h1')!.textContent).toBe('Card not found');
    const unknown = text();

    TestBed.resetTestingModule();
    server.seed('other-kitchen', [boardCard('theirs', 'ToDo', { title: 'Their secret plan' })]);
    await mount('theirs');

    expect(text()).toBe(unknown);
    expect(text()).not.toContain('Their secret plan');
    expect(root().querySelector('a[cpBackLink]')).not.toBeNull();
  });

  // AU.7: the page arriving, said once; the title slot holds the route's title, never a loading sentence.
  it('draws the card arriving while it is read, under the route’s own title', async () => {
    spyOn(server, 'detail').and.returnValue(NEVER);
    await mount();

    expect(root().querySelector('h1')!.textContent).toBe('Card');
    const status = root().querySelector('p.cp-sr-only[role="status"]')!;
    expect(status.textContent).toBe('Loading the card…');
    const busy = root().querySelector('.loading[aria-busy="true"]')!;
    expect(busy.querySelectorAll('cp-card cp-skeleton').length).toBe(2);
    expect(busy.textContent!.trim()).toBe('');
  });

  it('empties that status once the card is read', async () => {
    await mount();

    expect(root().querySelector('p.cp-sr-only[role="status"]')!.textContent).toBe('');
    expect(root().querySelector('.loading')).toBeNull();
  });

  it('says the card could not be loaded, and reads it again on request', async () => {
    server.offline = true;
    await mount();
    expect(root().querySelector('[role="alert"]')!.textContent).toContain("couldn't be loaded");

    server.offline = false;
    button('Try again').click();
    await settle();
    expect(root().querySelector('h1')!.textContent).toBe('Soda bread');
  });

  // ---- Editing ----

  describe('editing', () => {
    it('opens in a drawer that starts from the card as it is', async () => {
      await mount();
      await openEdit();

      expect(drawer()!.textContent).toContain('Edit card');
      expect(document.body.querySelector<HTMLInputElement>('#content-card-edit-title')!.value).toBe('Soda bread');
      expect(document.body.querySelector<HTMLTextAreaElement>('#content-card-edit-description')!.value).toBe(
        'Hero shot.\nThen the three posts.',
      );
      expect(document.body.querySelector<HTMLSelectElement>('#content-card-edit-channel')!.value).toBe('instagram');
      expect(drawer()!.textContent).toContain('No changes');
      expect(button('Save changes', document.body).disabled).toBe(true);
    });

    it('sends only what changed, then shows the card as the server has it', async () => {
      await mount();
      await openEdit();
      type('content-card-edit-title', 'Soda bread, for Friday');
      await settle();
      expect(drawer()!.textContent).toContain('Unsaved changes');

      button('Save changes', document.body).click();
      await settle();

      expect(server.updates.length).toBe(1);
      expect(server.updates[0].before.title).toBe('Soda bread');
      expect(server.updates[0].after.title).toBe('Soda bread, for Friday');
      expect(server.updates[0].after.description).toBe(server.updates[0].before.description);
      expect(server.updates[0].token).toBe('a-t1');
      expect(drawer()).toBeNull();
      expect(root().querySelector('h1')!.textContent).toBe('Soda bread, for Friday');
      expect(root().querySelector('.outcome')!.textContent).toContain('Your changes were saved.');
      expect(document.activeElement).toBe(button('Edit card'));
    });

    it('clears a field that was emptied', async () => {
      await mount();
      await openEdit();
      type('content-card-edit-description', '   ');
      await settle();
      button('Save changes', document.body).click();
      await settle();

      expect(server.updates[0].after.description).toBeNull();
      expect(topic('The plan').textContent).toContain('No notes on this card yet.');
    });

    it('does not overwrite a card that changed underneath: it keeps what was typed and loads the latest', async () => {
      await mount();
      await openEdit();
      type('content-card-edit-title', 'My new title');
      await settle();

      // A collaborator changes the channel while this drawer is open.
      server.touch(SLUG, 'a', { channelKey: null });
      button('Save changes', document.body).click();
      await settle();

      expect(drawer()).not.toBeNull();
      expect(drawer()!.textContent).toContain('This card changed while you were editing');
      expect(document.body.querySelector<HTMLInputElement>('#content-card-edit-title')!.value).toBe('My new title');
      expect(server.stored(SLUG, 'InProcess')[0].title).toBe('Soda bread');

      // Saved again, against the card as it now is: the title goes, and the collaborator's change stands.
      button('Save changes', document.body).click();
      await settle();

      expect(drawer()).toBeNull();
      expect(server.updates[1].token).not.toBe('a-t1');
      expect(server.stored(SLUG, 'InProcess')[0].title).toBe('My new title');
    });

    it('asks for a title rather than sending an empty one', async () => {
      await mount();
      await openEdit();
      type('content-card-edit-title', '  ');
      await settle();
      button('Save changes', document.body).click();
      await settle();

      expect(server.updates).toEqual([]);
      expect(drawer()!.textContent).toContain('A card needs a title.');
    });

    it('keeps what was typed and says why when the save is refused', async () => {
      await mount();
      await openEdit();
      type('content-card-edit-title', 'Not allowed');
      await settle();
      server.refuseNext = 'forbidden';
      button('Save changes', document.body).click();
      await settle();

      expect(drawer()!.querySelector('[role="alert"]')!.textContent).toContain('do not have permission to edit cards');
      expect(document.body.querySelector<HTMLInputElement>('#content-card-edit-title')!.value).toBe('Not allowed');
    });

    it('asks before discarding changes, and closes at once when there are none', async () => {
      await mount();
      await openEdit();
      button('Cancel', document.body).click();
      await settle();
      expect(asked).toEqual([]);
      expect(drawer()).toBeNull();

      await openEdit();
      type('content-card-edit-title', 'Half an edit');
      await settle();
      confirmAnswer = false;
      button('Cancel', document.body).click();
      await settle();
      expect(asked.length).toBe(1);
      expect(drawer()).not.toBeNull();

      confirmAnswer = true;
      button('Cancel', document.body).click();
      await settle();
      expect(drawer()).toBeNull();
      expect(server.updates).toEqual([]);
    });
  });

  // ---- Deleting ----

  describe('deleting', () => {
    it('says what goes and what stays before it asks, and keeps the card when the answer is no', async () => {
      await mount();
      confirmAnswer = false;

      button('Delete card').click();
      await settle();

      expect(asked[0].title).toBe('Delete “Soda bread”?');
      expect(asked[0].message).toContain('Its picture stays in your library');
      expect(asked[0].tone).toBe('danger');
      expect(server.removes).toEqual([]);
      expect(root().querySelector('h1')!.textContent).toBe('Soda bread');
    });

    it('deletes the card with its token and a key, and points back to the board', async () => {
      await mount();

      button('Delete card').click();
      await settle();

      expect(server.removes.length).toBe(1);
      expect(server.removes[0].token).toBe('a-t1');
      expect(server.removes[0].key).not.toBe('');
      expect(root().querySelector('h1')!.textContent).toBe('Card deleted');
      expect(text()).toContain('Nothing it pointed at was removed.');
      expect(root().querySelector('cp-notice[role="status"] a')!.getAttribute('href')).toBe(`/${SLUG}/content-board`);
    });

    it('does not delete a card that changed since it was read, and shows what changed', async () => {
      await mount();
      server.touch(SLUG, 'a', { title: 'Soda bread, revised' });

      button('Delete card').click();
      await settle();

      expect(root().querySelector('[role="alert"]')!.textContent).toContain('so it was not deleted');
      expect(root().querySelector('h1')!.textContent).toBe('Soda bread, revised');
      expect(server.stored(SLUG, 'InProcess').length).toBe(1);
    });

    it('reuses its key for a retry after a failure, and treats “already gone” as done', async () => {
      await mount();
      server.refuseDeleteWith = 'unavailable';

      button('Delete card').click();
      await settle();
      expect(root().querySelector('[role="alert"]')!.textContent).toContain('safe to try again');

      // The first attempt did land; only its answer was lost.
      server.erase(SLUG, 'a');
      button('Delete card').click();
      await settle();

      expect(server.removes[0].key).toBe(server.removes[1].key);
      expect(root().querySelector('h1')!.textContent).toBe('Card deleted');
    });

    it('says who may delete cards when this member may not', async () => {
      await mount();
      server.refuseDeleteWith = 'forbidden';

      button('Delete card').click();
      await settle();

      expect(root().querySelector('[role="alert"]')!.textContent).toContain('do not have permission to delete cards');
      expect(root().querySelector('h1')!.textContent).toBe('Soda bread');
    });
  });
});
