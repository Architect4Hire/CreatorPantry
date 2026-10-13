import { CdkDragDrop } from '@angular/cdk/drag-drop';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { ContentBoardColumn, ContentCard } from '../../models/content-board.models';
import { BrandProfileService, ContentChannelsOutcome } from '../../services/brand-profile.service';
import { FakeContentBoardService, boardCard } from '../../services/content-board.fake';
import { ContentBoardService } from '../../services/content-board.service';
import { ContentBoardComponent } from './content-board.component';
import { BOARD_PAGE_SIZE } from './content-board-state';

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'blog', displayName: 'Blog', isActive: true },
  { key: 'vine', displayName: 'Vine', isActive: false },
];

@Component({ template: '<p>card page</p>' })
class DetailStubComponent {}

describe('ContentBoardComponent', () => {
  let server: FakeContentBoardService;
  let harness: RouterTestingHarness;
  let channelsOutcome: ContentChannelsOutcome;
  let confirmAnswer: boolean;
  let confirms: number;

  const root = (): HTMLElement => harness.routeNativeElement!;
  const text = (): string => (root().textContent ?? '').replace(/\s+/g, ' ');

  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i += 1) {
      harness.detectChanges();
      await harness.fixture.whenStable();
    }
    harness.detectChanges();
  }

  async function mount(url = `/${SLUG}/content-board`): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/content-board', pathMatch: 'full', component: ContentBoardComponent, data: { title: 'Content Board' } },
          { path: ':workspaceSlug/content-board/:cardId', component: DetailStubComponent },
        ]),
        { provide: ContentBoardService, useValue: server },
        { provide: BrandProfileService, useValue: { listContentChannels: () => Promise.resolve(channelsOutcome) } },
        {
          provide: ConfirmService,
          useValue: {
            confirm: () => {
              confirms += 1;

              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, ContentBoardComponent);
    await settle();
  }

  function seed(): void {
    server.seed(SLUG, [
      boardCard('a', 'ToDo', { title: 'Soda bread', channelKey: 'instagram', plannedDate: '2026-10-16', plannedDay: 'Friday' }),
      boardCard('b', 'ToDo', { title: 'Ramen', descriptionPreview: 'Weeknight version.' }),
      boardCard('c', 'InProcess', { title: 'Cookies', moveCount: 1, hasPackage: true }),
      boardCard('d', 'Approved', {
        title: 'Granola',
        publishing: { publicationCount: 1, publishedCount: 1, lastPublishedAt: '2026-10-12T09:00:00Z' },
      }),
    ]);
  }

  const columns = (): HTMLElement[] => Array.from(root().querySelectorAll('cp-board-column'));
  const column = (name: string): HTMLElement =>
    columns().find((each) => each.querySelector('.heading')?.textContent === name)!;
  const titles = (name: string): string[] =>
    Array.from(column(name).querySelectorAll('.title a')).map((link) => link.textContent ?? '');
  const cardEl = (id: string): HTMLElement => root().querySelector(`[data-card-id="${id}"]`)!;
  const outcome = (): HTMLElement => root().querySelector('.outcome')!;
  const button = (name: string, within: ParentNode = root()): HTMLButtonElement =>
    Array.from(within.querySelectorAll('button')).find(
      (each) => each.getAttribute('aria-label') === name || each.textContent?.trim() === name,
    )!;
  const select = (id: string): HTMLSelectElement => root().querySelector(`#${id}`)!;

  function choose(control: HTMLSelectElement, value: string): void {
    control.value = value;
    control.dispatchEvent(new Event('change'));
  }

  /** Drop a card as the drag library reports it: the list it left, the list it landed in, and where. */
  async function drop(card: ContentCard, to: ContentBoardColumn, currentIndex: number, previousIndex = 0): Promise<void> {
    const lists = { from: { data: card.column }, to: { data: to } };
    const event = {
      item: { data: card },
      container: lists.to,
      previousContainer: card.column === to ? lists.to : lists.from,
      previousIndex,
      currentIndex,
    } as unknown as CdkDragDrop<ContentBoardColumn, ContentBoardColumn, ContentCard>;

    await (harness.routeDebugElement!.componentInstance as { onDrop(event: unknown): Promise<void> }).onDrop(event);
    await settle();
  }

  beforeEach(() => {
    server = new FakeContentBoardService();
    seed();
    channelsOutcome = { status: 'found', channels: CHANNELS };
    confirmAnswer = true;
    confirms = 0;
  });

  // ---- Reading ----

  describe('the board', () => {
    it('is a library: one head with the one action, then filters, then the cards in four named columns', async () => {
      await mount();

      expect(root().querySelectorAll('h1').length).toBe(1);
      expect(root().querySelector('h1')!.textContent).toBe('Content Board');
      expect(button('New card', root().querySelector('cp-page-head')!)).toBeTruthy();
      expect(columns().map((each) => each.querySelector('.heading')!.textContent)).toEqual([
        'To do',
        'In process',
        'Ready for review',
        'Approved',
      ]);
      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
      expect(titles('In process')).toEqual(['Cookies']);
      expect(text()).toContain('4 cards on the board.');
    });

    it('names each column as a region and says its count in words', async () => {
      await mount();

      const region = column('To do').querySelector('section')!;
      expect(region.getAttribute('aria-labelledby')).toBe(column('To do').querySelector('.heading')!.id);
      expect(column('To do').querySelector('.count .sr-only')!.textContent).toBe('2 cards');
      expect(column('In process').querySelector('.count .sr-only')!.textContent).toBe('1 card');
      expect(column('To do').querySelector('ul')!.getAttribute('aria-label')).toBe('To do cards');

      // The outline runs page, list, column, card: no two levels share a heading.
      expect(root().querySelector('cp-list-shell h2')!.textContent).toBe('Cards');
      expect(column('To do').querySelector('.heading')!.tagName).toBe('H3');
      expect(cardEl('a').querySelector('.title')!.tagName).toBe('H4');
    });

    it('says why an empty column is empty, and keeps it there to be dropped onto', async () => {
      await mount();

      const empty = column('Ready for review');
      expect(empty.textContent).toContain('Nothing here yet.');
      expect(empty.querySelector('.count [aria-hidden="true"]')!.textContent).toBe('0');
      expect(empty.querySelector('ul')).not.toBeNull();
    });

    it('shows a card’s state in words, never by colour alone', async () => {
      await mount();

      expect(cardEl('a').querySelector('cp-status-pill')!.textContent).toContain('Not scheduled');
      expect(cardEl('d').querySelector('cp-status-pill')!.textContent).toContain('Published');
      expect(cardEl('a').textContent).toContain('Instagram');
      expect(cardEl('c').textContent).toContain('Recipe package');
      expect(cardEl('b').textContent).toContain('Weeknight version.');
      // A calendar date, shown as the day it names whatever zone the browser is in.
      expect(cardEl('a').textContent).toContain('Fri, Oct 16, 2026');
    });

    it('links each card’s title to the card’s own page', async () => {
      await mount();

      expect(cardEl('a').querySelector('.title a')!.getAttribute('href')).toBe(`/${SLUG}/content-board/a`);
    });

    it('offers the first card to a board with nothing on it', async () => {
      server.seed(SLUG, []);
      await mount();

      expect(text()).toContain('Nothing planned yet');
      expect(columns().length).toBe(0);
      expect(button('New card', root().querySelector('cp-empty-state')!)).toBeTruthy();
    });

    it('says the board could not be loaded, keeps the way to add a card, and reads again on request', async () => {
      server.offline = true;
      await mount();

      expect(root().querySelector('[role="alert"]')!.textContent).toContain("Couldn't load your content board");
      expect(button('New card')).toBeTruthy();

      server.offline = false;
      button('Try again').click();
      await settle();

      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
    });

    it('keeps the other three columns when one cannot be loaded, and retries that one alone', async () => {
      server.unreadable.add('InProcess');
      await mount();

      expect(column('In process').querySelector('[role="alert"]')!.textContent).toContain("couldn't be loaded");
      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);

      server.unreadable.delete('InProcess');
      server.lists.length = 0;
      button('Try loading In process again').click();
      await settle();

      expect(titles('In process')).toEqual(['Cookies']);
      expect(server.lists.map((read) => read.column)).toEqual(['InProcess']);
    });

    it('loads more of one column on its own, and says so when that fails', async () => {
      server.seed(
        SLUG,
        Array.from({ length: BOARD_PAGE_SIZE + 2 }, (_, index) => boardCard(`t${index}`, 'ToDo')),
      );
      await mount();

      expect(column('To do').querySelector('.count [aria-hidden="true"]')!.textContent).toBe(String(BOARD_PAGE_SIZE + 2));
      expect(titles('To do').length).toBe(BOARD_PAGE_SIZE);

      server.offline = true;
      button('Load more To do cards').click();
      await settle();
      expect(column('To do').querySelector('[role="alert"]')!.textContent).toContain("Couldn't load more");
      expect(titles('To do').length).toBe(BOARD_PAGE_SIZE);

      server.offline = false;
      button('Load more To do cards').click();
      await settle();
      expect(titles('To do').length).toBe(BOARD_PAGE_SIZE + 2);
      expect(column('To do').querySelector('.footer button')).toBeNull();
    });
  });

  // ---- Filters ----

  describe('filters', () => {
    it('are read from the address and sent to the server, column by column', async () => {
      await mount(`/${SLUG}/content-board?channel=instagram&day=Friday&date=2026-10-16`);

      expect(server.lists.every((read) => read.filters.channel === 'instagram')).toBe(true);
      expect(server.lists.every((read) => read.filters.day === 'Friday' && read.filters.date === '2026-10-16')).toBe(true);
      expect(titles('To do')).toEqual(['Soda bread']);
      expect(text()).toContain('1 card match these filters.');
      expect(column('In process').textContent).toContain('No cards here match these filters.');
    });

    it('go into the address when chosen, and the board is read again', async () => {
      await mount();
      server.lists.length = 0;

      choose(root().querySelector('select[name="channel"]')!, 'instagram');
      await settle();

      expect(TestBed.inject(Router).url).toBe(`/${SLUG}/content-board?channel=instagram`);
      expect(server.lists.length).toBe(4);
      expect(titles('To do')).toEqual(['Soda bread']);

      button('Clear filters').click();
      await settle();
      expect(TestBed.inject(Router).url).toBe(`/${SLUG}/content-board`);
      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
    });

    it('offer every channel the catalogue lists, by name', async () => {
      await mount();

      const options = Array.from(root().querySelectorAll('select[name="channel"] option')).map((each) => each.textContent);
      expect(options).toEqual(['All channels', 'Instagram', 'Blog', 'Vine']);
    });

    it('drop a value the board could not have written rather than send it', async () => {
      await mount(`/${SLUG}/content-board?day=Someday&date=tomorrow`);

      expect(server.lists.every((read) => read.filters.day === null && read.filters.date === null)).toBe(true);
    });

    it('say so when nothing matches, and offer the way back', async () => {
      await mount(`/${SLUG}/content-board?channel=blog`);

      expect(text()).toContain('No cards match these filters');
      button('Clear filters', root().querySelector('cp-empty-state')!).click();
      await settle();
      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
    });

    it('keep the cards listed when channel names cannot be loaded', async () => {
      channelsOutcome = { status: 'unavailable' };
      await mount();

      expect(text()).toContain("Channel names couldn't be loaded");
      expect((root().querySelector('select[name="channel"]') as HTMLSelectElement).disabled).toBe(true);
      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
      // The key stands in for the name it could not read.
      expect(cardEl('a').textContent).toContain('instagram');
    });
  });

  // ---- Moving from the keyboard ----

  describe('moving with the Move control', () => {
    it('is on every card, named for the card it moves', async () => {
      await mount();

      for (const [id, title] of [['a', 'Soda bread'], ['b', 'Ramen'], ['c', 'Cookies'], ['d', 'Granola']]) {
        expect(button(`Move ${title}`, cardEl(id))).toBeTruthy();
      }
    });

    it('opens a form that starts from where the card is, and offers every column', async () => {
      await mount();

      button('Move Soda bread').click();
      await settle();

      const dialog = root().querySelector('[role="dialog"]')!;
      expect(dialog.querySelector('h2')!.textContent).toBe('Move “Soda bread”');
      expect(select('content-card-move-column').value).toBe('ToDo');
      expect(Array.from(select('content-card-move-column').options).map((each) => each.textContent)).toEqual([
        'To do',
        'In process',
        'Ready for review',
        'Approved',
      ]);
      // Where it is now: before Ramen. The card itself is not offered as a place.
      expect(Array.from(select('content-card-move-place').options).map((each) => each.textContent)).toEqual([
        'Before “Ramen”',
        'At the bottom',
      ]);
      expect(select('content-card-move-place').value).toBe('b');
      // A note belongs to a change of column, and there is not one yet.
      expect(root().querySelector('#content-card-move-note')).toBeNull();
    });

    it('moves the card to another column before a named card, with a note, and says so', async () => {
      await mount();
      button('Move Soda bread').click();
      await settle();

      select('content-card-move-column').value = 'InProcess';
      select('content-card-move-column').dispatchEvent(new Event('change'));
      await settle();
      expect(select('content-card-move-place').value).toBe('');

      select('content-card-move-place').value = 'c';
      select('content-card-move-place').dispatchEvent(new Event('change'));
      const note = root().querySelector<HTMLTextAreaElement>('#content-card-move-note')!;
      note.value = 'Shoot booked';
      note.dispatchEvent(new Event('input'));
      await settle();

      button('Move card').click();
      await settle();

      expect(server.moves).toEqual([
        { slug: SLUG, cardId: 'a', place: { column: 'InProcess', beforeCardId: 'c', note: 'Shoot booked' }, token: 'a-t1' },
      ]);
      expect(titles('In process')).toEqual(['Soda bread', 'Cookies']);
      expect(titles('To do')).toEqual(['Ramen']);
      expect(root().querySelector('[role="dialog"]')).toBeNull();

      expect(outcome().getAttribute('role')).toBe('status');
      expect(outcome().textContent).toContain('Moved “Soda bread” to In process, before “Cookies”.');
      // Focus follows the card to where it now is.
      expect(document.activeElement).toBe(cardEl('a').querySelector('.title a'));
    });

    it('reorders within a column without a note', async () => {
      await mount();
      button('Move Ramen').click();
      await settle();

      select('content-card-move-place').value = 'a';
      select('content-card-move-place').dispatchEvent(new Event('change'));
      await settle();
      button('Move card').click();
      await settle();

      expect(server.moves[0].place).toEqual({ column: 'ToDo', beforeCardId: 'a', note: null });
      expect(titles('To do')).toEqual(['Ramen', 'Soda bread']);
    });

    it('sends nothing when the card is left where it is', async () => {
      await mount();
      button('Move Soda bread').click();
      await settle();

      button('Move card').click();
      await settle();

      expect(server.moves).toEqual([]);
      expect(outcome().textContent).toContain('“Soda bread” is already there.');
      expect(outcome().getAttribute('role')).toBe('status');
    });

    it('closes on Cancel and on Escape without moving anything', async () => {
      await mount();

      button('Move Soda bread').click();
      await settle();
      button('Cancel').click();
      await settle();
      expect(root().querySelector('[role="dialog"]')).toBeNull();

      button('Move Soda bread').click();
      await settle();
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      await settle();
      expect(root().querySelector('[role="dialog"]')).toBeNull();
      expect(server.moves).toEqual([]);
    });

    it('says when a column is only partly on screen, because “the bottom” is below what is shown', async () => {
      server.seed(SLUG, [
        boardCard('a', 'InProcess', { title: 'Soda bread' }),
        ...Array.from({ length: BOARD_PAGE_SIZE + 2 }, (_, index) => boardCard(`t${index}`, 'ToDo')),
      ]);
      await mount();

      button('Move Soda bread').click();
      await settle();
      select('content-card-move-column').value = 'ToDo';
      select('content-card-move-column').dispatchEvent(new Event('change'));
      await settle();

      expect(root().querySelector('[role="dialog"]')!.textContent).toContain('including the ones not shown yet');
    });
  });

  // ---- Moving by dragging ----

  describe('moving by dragging', () => {
    it('asks the server the same question a Move form would', async () => {
      await mount();
      const card = server.stored(SLUG, 'ToDo')[0];

      // Dropped second in "In process": after Cookies, which is the bottom.
      await drop(card, 'InProcess', 1);

      expect(server.moves).toEqual([
        { slug: SLUG, cardId: 'a', place: { column: 'InProcess', beforeCardId: null }, token: 'a-t1' },
      ]);
      expect(titles('In process')).toEqual(['Cookies', 'Soda bread']);
      expect(outcome().textContent).toContain('Moved “Soda bread” to the bottom of In process.');
    });

    it('names the card it was dropped before', async () => {
      await mount();

      await drop(server.stored(SLUG, 'ToDo')[1], 'ToDo', 0, 1);

      expect(server.moves[0].place).toEqual({ column: 'ToDo', beforeCardId: 'a' });
      expect(titles('To do')).toEqual(['Ramen', 'Soda bread']);
    });

    it('sends nothing for a card put back where it was picked up', async () => {
      await mount();

      await drop(server.stored(SLUG, 'ToDo')[0], 'ToDo', 0, 0);

      expect(server.moves).toEqual([]);
    });

    it('is switched off, with every Move control, while a move is being saved', async () => {
      await mount();

      const pending = drop(server.stored(SLUG, 'ToDo')[0], 'InProcess', 0);
      harness.detectChanges();

      expect(button('Move Ramen').disabled).toBe(true);
      expect(cardEl('a').textContent).toContain('Saving its new place…');
      expect(cardEl('a').querySelector('article')!.getAttribute('aria-busy')).toBe('true');

      await pending;
      expect(button('Move Ramen').disabled).toBe(false);
      expect(cardEl('a').querySelector('article')!.hasAttribute('aria-busy')).toBe(false);
    });
  });

  // ---- When the server says no ----

  describe('a move the server does not make', () => {
    it('puts the card back, reads the board again and says it was not moved, when the board had changed', async () => {
      await mount();
      const card = server.stored(SLUG, 'ToDo')[0];
      server.touch(SLUG, 'a', { title: 'Soda bread, revised' });

      await drop(card, 'InProcess', 0);

      expect(titles('To do')).toEqual(['Soda bread, revised', 'Ramen']);
      expect(titles('In process')).toEqual(['Cookies']);
      expect(outcome().getAttribute('role')).toBe('alert');
      expect(outcome().textContent).toContain('The board changed before “Soda bread” could be moved, so it was not.');
    });

    it('puts the card back when the card it was to go before has gone from that column', async () => {
      await mount();
      const card = server.stored(SLUG, 'ToDo')[0];
      server.erase(SLUG, 'c');

      await drop(card, 'InProcess', 0);

      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
      expect(titles('In process')).toEqual([]);
      expect(outcome().getAttribute('role')).toBe('alert');
      expect(outcome().textContent).toContain('could not be put there, so it has not moved');
    });

    it('takes a deleted card off the board and says it was deleted', async () => {
      await mount();
      const card = server.stored(SLUG, 'ToDo')[0];
      server.erase(SLUG, 'a');

      await drop(card, 'Approved', 0);

      expect(titles('To do')).toEqual(['Ramen']);
      expect(titles('Approved')).toEqual(['Granola']);
      expect(outcome().textContent).toContain('“Soda bread” has been deleted');
    });

    it('puts the card back and says who may move cards, when this member may not', async () => {
      await mount();
      server.refuseNext = 'forbidden';

      await drop(server.stored(SLUG, 'ToDo')[0], 'Approved', 0);

      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
      expect(outcome().getAttribute('role')).toBe('alert');
      expect(outcome().textContent).toContain('You do not have permission to move cards');
    });

    it('puts the card back when the request did not get through, and the same move can be made again', async () => {
      await mount();
      const card = server.stored(SLUG, 'ToDo')[0];
      server.offline = true;

      await drop(card, 'InProcess', 0);

      expect(titles('To do')).toEqual(['Soda bread', 'Ramen']);
      expect(outcome().textContent).toContain('Nothing was changed');

      server.offline = false;
      await drop(card, 'InProcess', 0);
      expect(titles('In process')).toEqual(['Soda bread', 'Cookies']);
      expect(outcome().getAttribute('role')).toBe('status');
    });
  });

  // ---- Creating ----

  describe('a new card', () => {
    async function openForm(): Promise<void> {
      button('New card').click();
      await settle();
    }

    function type(id: string, value: string): void {
      const control = root().querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!;
      control.value = value;
      control.dispatchEvent(new Event('input'));
    }

    it('is made from a short form and appears at the bottom of its column', async () => {
      await mount();
      await openForm();

      expect(root().querySelector('[role="dialog"] h2')!.textContent).toBe('New card');
      type('content-card-create-title', '  Lemon tart  ');
      type('content-card-create-description', 'Overhead, with the zest.');
      await settle();
      button('Add card').click();
      await settle();

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].fields).toEqual({
        title: 'Lemon tart',
        description: 'Overhead, with the zest.',
        channelKey: null,
        plannedDate: null,
      });
      expect(server.creates[0].column).toBe('ToDo');
      expect(root().querySelector('[role="dialog"]')).toBeNull();
      expect(titles('To do')).toEqual(['Soda bread', 'Ramen', 'Lemon tart']);
      expect(outcome().textContent).toContain('Added “Lemon tart” to To do.');
    });

    it('offers only channels that can be chosen now', async () => {
      await mount();
      await openForm();

      const options = Array.from(select('content-card-create-channel').options).map((each) => each.textContent);
      expect(options).toEqual(['No channel in particular', 'Instagram', 'Blog']);
    });

    it('asks for a title before sending anything', async () => {
      await mount();
      await openForm();

      button('Add card').click();
      await settle();

      expect(server.creates).toEqual([]);
      expect(root().querySelector('[role="dialog"]')!.textContent).toContain('A card needs a title.');
    });

    it('keeps what was typed when the server refuses, and shows its message on the field', async () => {
      await mount();
      await openForm();
      type('content-card-create-title', 'Lemon tart');
      await settle();

      server.refuseNext = 'forbidden';
      button('Add card').click();
      await settle();

      expect(root().querySelector('[role="dialog"] [role="alert"]')!.textContent).toContain('do not have permission');
      expect(root().querySelector<HTMLInputElement>('#content-card-create-title')!.value).toBe('Lemon tart');
    });

    it('reuses its key for a retry, so a save whose answer was lost is not a second card', async () => {
      await mount();
      await openForm();
      type('content-card-create-title', 'Lemon tart');
      await settle();

      server.refuseNext = 'unavailable';
      button('Add card').click();
      await settle();
      button('Add card').click();
      await settle();

      expect(server.creates.length).toBe(2);
      expect(server.creates[0].key).toBe(server.creates[1].key);
      expect(titles('To do').filter((title) => title === 'Lemon tart').length).toBe(1);
    });

    it('takes a new key once the card has been changed', async () => {
      await mount();
      await openForm();
      type('content-card-create-title', 'Lemon tart');
      await settle();
      server.refuseNext = 'unavailable';
      button('Add card').click();
      await settle();

      type('content-card-create-title', 'Lemon tart, glazed');
      await settle();
      button('Add card').click();
      await settle();

      expect(server.creates[0].key).not.toBe(server.creates[1].key);
    });

    it('asks before discarding what was typed, and closes at once when nothing was', async () => {
      await mount();
      await openForm();
      button('Cancel').click();
      await settle();
      expect(confirms).toBe(0);
      expect(root().querySelector('[role="dialog"]')).toBeNull();

      await openForm();
      type('content-card-create-title', 'Lemon tart');
      await settle();
      confirmAnswer = false;
      button('Cancel').click();
      await settle();
      expect(confirms).toBe(1);
      expect(root().querySelector<HTMLInputElement>('#content-card-create-title')!.value).toBe('Lemon tart');

      confirmAnswer = true;
      button('Cancel').click();
      await settle();
      expect(root().querySelector('[role="dialog"]')).toBeNull();
    });

    it('says where a new card went when the filters in use do not show it', async () => {
      await mount(`/${SLUG}/content-board?channel=instagram`);
      await openForm();
      type('content-card-create-title', 'Lemon tart');
      await settle();
      button('Add card').click();
      await settle();

      expect(titles('To do')).toEqual(['Soda bread']);
      expect(outcome().textContent).toContain('It is not shown, because it does not match the filters in use');
    });
  });

  // ---- Two workspaces ----

  it('shows nothing of one workspace once the address names another', async () => {
    server.seed(OTHER_SLUG, [boardCard('z', 'ToDo', { title: 'Their plan' })]);
    await mount();
    await drop(server.stored(SLUG, 'ToDo')[0], 'InProcess', 0);
    expect(outcome().textContent).toContain('Soda bread');

    await harness.navigateByUrl(`/${OTHER_SLUG}/content-board`);
    await settle();

    expect(text()).not.toContain('Soda bread');
    expect(text()).not.toContain('Cookies');
    expect(titles('To do')).toEqual(['Their plan']);
    expect(titles('In process')).toEqual([]);
    expect(server.lists.slice(-4).every((read) => read.slug === OTHER_SLUG)).toBe(true);
  });
});
