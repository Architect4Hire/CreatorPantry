/**
 * How many rows a list shows before the creator asks for more, or starts typing in its filter.
 *
 * The one number every list that has a {@link ListFilterComponent} previews, so the hub's work in progress and a
 * workflow's "Which recipe?" read alike.
 */
export const LIST_PREVIEW_ROWS = 5;

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
