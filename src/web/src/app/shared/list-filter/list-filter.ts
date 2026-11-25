/**
 * How many rows a list shows on one page, in a {@link ListFilterComponent}'s pager.
 *
 * The one number every list that has a filter pages by, so the hub's work in progress and a workflow's "Which
 * recipe?" read alike.
 */
export const LIST_PAGE_SIZE = 10;

/** How many pages `total` rows make at `size` to a page. Never fewer than one: an empty list is still on page one. */
export function pageCount(total: number, size: number = LIST_PAGE_SIZE): number {
  return Math.max(1, Math.ceil(total / size));
}

/**
 * The rows on a page, counting from 1. A page past the end is the last one and one before the start is the first,
 * so a list that shrinks under the creator (a run stopped, a filter typed) never lands on a page that is not there.
 */
export function pageSlice<T>(rows: readonly T[], page: number, size: number = LIST_PAGE_SIZE): readonly T[] {
  const at = Math.min(Math.max(1, Math.trunc(page) || 1), pageCount(rows.length, size));
  return rows.slice((at - 1) * size, at * size);
}

/** The words in a filter box, lower-cased. Empty when nothing has been typed — which is "no filter", not "match nothing". */
export function searchWords(query: string): readonly string[] {
  return query
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter((word) => word !== '');
}

/** Whether `text` holds every one of `words`, in any order and anywhere in it. No words matches everything. */
export function matchesWords(text: string, words: readonly string[]): boolean {
  const lowered = text.toLowerCase();
  return words.every((word) => lowered.includes(word));
}
