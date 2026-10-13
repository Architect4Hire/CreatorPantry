import { ContentBoardColumn, NO_BOARD_FILTERS } from '../../models/content-board.models';
import { FakeContentBoardService, boardCard } from '../../services/content-board.fake';
import { ContentBoardService } from '../../services/content-board.service';
import { BOARD_PAGE_SIZE, ContentBoardState } from './content-board-state';

const SLUG = 'cozy-fall';

let server: FakeContentBoardService;
let board: ContentBoardState;

function seed(): void {
  server.seed(SLUG, [
    boardCard('a', 'ToDo', { title: 'Soda bread' }),
    boardCard('b', 'ToDo', { title: 'Ramen' }),
    boardCard('c', 'ToDo', { title: 'Lemon tart' }),
    boardCard('d', 'InProcess', { title: 'Cookies' }),
    boardCard('e', 'Approved', { title: 'Granola' }),
  ]);
}

const ids = (column: ContentBoardColumn): string[] => board.column(column).cards.map((card) => card.id);
const stored = (column: ContentBoardColumn): string[] => server.stored(SLUG, column).map((card) => card.id);

describe('ContentBoardState', () => {
  beforeEach(async () => {
    server = new FakeContentBoardService();
    seed();
    board = new ContentBoardState(server as unknown as ContentBoardService);
    await board.open(SLUG, NO_BOARD_FILTERS);
  });

  describe('reading', () => {
    it('reads each column on its own, in the server’s order, with its count', () => {
      expect(server.lists.map((read) => read.column).sort()).toEqual(['Approved', 'InProcess', 'ReadyForReview', 'ToDo']);
      expect(ids('ToDo')).toEqual(['a', 'b', 'c']);
      expect(board.column('ToDo').count).toBe(3);
      expect(board.column('ReadyForReview').cards).toEqual([]);
      expect(board.column('ReadyForReview').count).toBe(0);
      expect(board.total()).toBe(5);
      expect(board.loading()).toBe(false);
    });

    it('lets one column fail without taking the others with it', async () => {
      server.unreadable.add('InProcess');
      await board.open(SLUG, NO_BOARD_FILTERS);

      expect(board.column('InProcess').phase).toBe('unavailable');
      expect(board.column('ToDo').phase).toBe('ready');
      expect(board.unavailable()).toBe(false);

      server.unreadable.delete('InProcess');
      await board.reload('InProcess');
      expect(ids('InProcess')).toEqual(['d']);
    });

    it('says the whole board is unavailable only when no column answered', async () => {
      server.offline = true;
      await board.open(SLUG, NO_BOARD_FILTERS);

      expect(board.unavailable()).toBe(true);
    });

    it('appends the next page of one column and keeps the count the server gave', async () => {
      server.seed(
        SLUG,
        Array.from({ length: BOARD_PAGE_SIZE + 3 }, (_, index) => boardCard(`t${index}`, 'ToDo')),
      );
      await board.open(SLUG, NO_BOARD_FILTERS);

      expect(board.column('ToDo').cards.length).toBe(BOARD_PAGE_SIZE);
      expect(board.column('ToDo').count).toBe(BOARD_PAGE_SIZE + 3);
      expect(board.column('ToDo').nextCursor).not.toBeNull();

      await board.loadMore('ToDo');

      expect(board.column('ToDo').cards.length).toBe(BOARD_PAGE_SIZE + 3);
      expect(board.column('ToDo').nextCursor).toBeNull();
    });

    it('keeps the cards it has when a later page fails, and says so', async () => {
      server.seed(
        SLUG,
        Array.from({ length: BOARD_PAGE_SIZE + 3 }, (_, index) => boardCard(`t${index}`, 'ToDo')),
      );
      await board.open(SLUG, NO_BOARD_FILTERS);

      server.offline = true;
      await board.loadMore('ToDo');

      expect(board.column('ToDo').cards.length).toBe(BOARD_PAGE_SIZE);
      expect(board.column('ToDo').moreFailed).toBe(true);
      expect(board.column('ToDo').phase).toBe('ready');
    });

    it('drops an answer for a board it has since left', async () => {
      server.seed('other-kitchen', [boardCard('z', 'ToDo')]);

      const first = board.open(SLUG, NO_BOARD_FILTERS);
      const second = board.open('other-kitchen', NO_BOARD_FILTERS);
      await Promise.all([first, second]);

      expect(ids('ToDo')).toEqual(['z']);
      expect(ids('InProcess')).toEqual([]);
    });
  });

  describe('moving', () => {
    it('moves a card to another column before a named card, and reads both columns again', async () => {
      server.lists.length = 0;

      const result = await board.move('a', { column: 'InProcess', beforeCardId: 'd' });

      expect(result.status).toBe('moved');
      expect(result.message).toBe('Moved “Soda bread” to In process, before “Cookies”.');
      expect(server.moves).toEqual([
        { slug: SLUG, cardId: 'a', place: { column: 'InProcess', beforeCardId: 'd' }, token: 'a-t1' },
      ]);
      expect(ids('InProcess')).toEqual(['a', 'd']);
      expect(ids('ToDo')).toEqual(['b', 'c']);
      expect(board.column('ToDo').count).toBe(2);
      expect(board.column('InProcess').count).toBe(2);
      expect(server.lists.map((read) => read.column).sort()).toEqual(['InProcess', 'ToDo']);

      // The card now carries the token the server gave it, so the next move is not stale.
      expect(board.find('a')!.concurrencyToken).not.toBe('a-t1');
      expect((await board.move('a', { column: 'Approved', beforeCardId: null })).status).toBe('moved');
    });

    it('reorders within a column and says where', async () => {
      const result = await board.move('c', { column: 'ToDo', beforeCardId: 'a' });

      expect(result.message).toBe('Moved “Lemon tart” to To do, before “Soda bread”.');
      expect(ids('ToDo')).toEqual(['c', 'a', 'b']);
      expect(stored('ToDo')).toEqual(['c', 'a', 'b']);
      expect(board.column('ToDo').count).toBe(3);
    });

    it('shows the card in its new place at once, while the move is still out', () => {
      const pending = board.move('a', { column: 'Approved', beforeCardId: 'e' });

      // Synchronously after the call: placed, counted, and marked as moving.
      expect(ids('Approved')).toEqual(['a', 'e']);
      expect(ids('ToDo')).toEqual(['b', 'c']);
      expect(board.column('Approved').count).toBe(2);
      expect(board.moving()).toBe('a');

      return pending.then(() => expect(board.moving()).toBeNull());
    });

    it('takes one move at a time', async () => {
      const first = board.move('a', { column: 'InProcess', beforeCardId: null });
      const second = await board.move('b', { column: 'InProcess', beforeCardId: null });

      expect(second.status).toBe('busy');
      await first;
      expect(server.moves.length).toBe(1);
      expect(ids('ToDo')).toEqual(['b', 'c']);
    });

    it('sends nothing for a card that is already exactly there', async () => {
      expect((await board.move('a', { column: 'ToDo', beforeCardId: 'b' })).status).toBe('unchanged');
      expect((await board.move('c', { column: 'ToDo', beforeCardId: null })).status).toBe('unchanged');
      expect((await board.move('a', { column: 'ToDo', beforeCardId: 'a' })).status).toBe('unchanged');
      expect(server.moves).toEqual([]);
    });

    it('does send a move to the bottom of a column it has only partly read', async () => {
      server.seed(
        SLUG,
        Array.from({ length: BOARD_PAGE_SIZE + 2 }, (_, index) => boardCard(`t${index}`, 'ToDo')),
      );
      await board.open(SLUG, NO_BOARD_FILTERS);
      const last = `t${BOARD_PAGE_SIZE - 1}`;

      // Last on screen, but not last in the column: "the bottom" is below cards this screen has not read.
      const result = await board.move(last, { column: 'ToDo', beforeCardId: null });

      expect(result.status).toBe('moved');
      expect(stored('ToDo').at(-1)).toBe(last);
    });
  });

  describe('when the server does not make the move', () => {
    it('puts the card back and reads the board again when the board had changed', async () => {
      // Somebody edits the card after this screen read it.
      server.touch(SLUG, 'a', { title: 'Soda bread, revised' });
      server.lists.length = 0;

      const result = await board.move('a', { column: 'InProcess', beforeCardId: 'd' });

      expect(result.status).toBe('stale');
      expect(result.message).toContain('was not');
      expect(stored('InProcess')).toEqual(['d']);
      expect(ids('InProcess')).toEqual(['d']);
      expect(ids('ToDo')).toEqual(['a', 'b', 'c']);
      expect(board.column('ToDo').count).toBe(3);
      expect(board.column('InProcess').count).toBe(1);

      // Read again, so the creator sees what changed and the same move now carries a current token.
      expect(server.lists.map((read) => read.column).sort()).toEqual(['InProcess', 'ToDo']);
      expect(board.find('a')!.title).toBe('Soda bread, revised');
      expect((await board.move('a', { column: 'InProcess', beforeCardId: 'd' })).status).toBe('moved');
    });

    it('puts the card back when the card it was to go before has left that column', async () => {
      // "d" moves on before this screen hears of it.
      server.seed(SLUG, [
        boardCard('a', 'ToDo', { title: 'Soda bread' }),
        boardCard('b', 'ToDo'),
        boardCard('c', 'ToDo'),
        boardCard('d', 'Approved', { title: 'Cookies' }),
        boardCard('e', 'Approved'),
      ]);

      const result = await board.move('a', { column: 'InProcess', beforeCardId: 'd' });

      expect(result.status).toBe('refused');
      expect(ids('ToDo')).toEqual(['a', 'b', 'c']);
      // The target column was read again, so the card that is no longer there is no longer shown there.
      expect(ids('InProcess')).toEqual([]);
      expect(stored('ToDo')).toEqual(['a', 'b', 'c']);
    });

    it('takes a deleted card off the board and says it was deleted', async () => {
      server.erase(SLUG, 'a');

      const result = await board.move('a', { column: 'InProcess', beforeCardId: null });

      expect(result.status).toBe('gone');
      expect(result.message).toContain('has been deleted');
      expect(ids('ToDo')).toEqual(['b', 'c']);
      expect(ids('InProcess')).toEqual(['d']);
      expect(board.column('ToDo').count).toBe(2);
    });

    it('puts the card back, unread and unchanged, when the caller may not move cards', async () => {
      server.refuseNext = 'forbidden';
      server.lists.length = 0;

      const result = await board.move('a', { column: 'InProcess', beforeCardId: null });

      expect(result.status).toBe('forbidden');
      expect(ids('ToDo')).toEqual(['a', 'b', 'c']);
      expect(ids('InProcess')).toEqual(['d']);
      expect(server.lists).toEqual([]);
    });

    it('puts the card back when the request did not get through, and the same move works later', async () => {
      server.offline = true;

      const failed = await board.move('a', { column: 'InProcess', beforeCardId: 'd' });

      expect(failed.status).toBe('unavailable');
      expect(failed.message).toContain('Nothing was changed');
      expect(ids('ToDo')).toEqual(['a', 'b', 'c']);
      expect(ids('InProcess')).toEqual(['d']);
      expect(board.moving()).toBeNull();

      server.offline = false;
      expect((await board.move('a', { column: 'InProcess', beforeCardId: 'd' })).status).toBe('moved');
    });

    it('keeps the server’s answer even when reading the columns again fails', async () => {
      const pending = board.move('a', { column: 'InProcess', beforeCardId: 'd' });
      // The move itself has been sent; everything after it fails.
      server.offline = true;
      const result = await pending;

      expect(result.status).toBe('moved');
      expect(ids('InProcess')).toEqual(['a', 'd']);
      expect(board.find('a')!.concurrencyToken).not.toBe('a-t1');
      expect(board.column('InProcess').phase).toBe('ready');
    });
  });

  it('drops a card and its count when told it is gone', () => {
    board.drop('b');

    expect(ids('ToDo')).toEqual(['a', 'c']);
    expect(board.column('ToDo').count).toBe(2);
  });
});
