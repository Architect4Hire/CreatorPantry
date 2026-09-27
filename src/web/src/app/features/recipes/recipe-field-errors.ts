/**
 * Where a refused save's field errors belong, for the two lists the server reports by position.
 *
 * The server validates each list as a whole for the rules that are about the whole list, and per position for
 * the rules that are about one group, line or step. So a refusal arrives as either a flat key —
 * `ingredientGroups`, `instructions` — or an indexed one:
 *
 * ```text
 * ingredientGroups[0].title
 * ingredientGroups[0].ingredients[2].quantity
 * instructions[1].steps[0].text
 * ```
 *
 * Nothing here decides whether anything is wrong. It reads a position the server sent and resolves it to the
 * row that was submitted at that position; every rule stays on the server, where there is one copy of it.
 */

/** A parsed indexed key. `lineIndex` is null for a key about the group itself rather than a row inside it. */
export interface RecipeErrorPosition {
  readonly list: 'ingredientGroups' | 'instructions';
  readonly groupIndex: number;
  readonly lineIndex: number | null;
  readonly field: string;
}

const POSITION_PATTERN = /^(ingredientGroups|instructions)\[(\d+)\](?:\.(?:ingredients|steps)\[(\d+)\])?\.([A-Za-z]+)$/;

/**
 * The list a key belongs to, whether it is flat or indexed.
 *
 * `ingredientGroups[0].ingredients[2].quantity` and `ingredientGroups` are the same tab's business, so the tab
 * markers and the reveal both read a key through this rather than matching it exactly.
 */
export function errorListKey(key: string): string {
  const bracket = key.indexOf('[');

  return bracket === -1 ? key : key.slice(0, bracket);
}

/** Reads an indexed key, or null for a flat one — or for a shape this client does not recognise. */
export function parseErrorPosition(key: string): RecipeErrorPosition | null {
  const match = POSITION_PATTERN.exec(key);
  if (match === null) return null;

  const [, list, group, line, field] = match;

  return {
    list: list as 'ingredientGroups' | 'instructions',
    groupIndex: Number(group),
    lineIndex: line === undefined ? null : Number(line),
    field,
  };
}

/**
 * The messages for each row, keyed by the row's own stable key.
 *
 * `submitted` is the positions as the request sent them — which is not the working copy, because a row with no
 * text is filtered out of the request. Given group 0 line 2, the answer is whatever row was third among those
 * group 0 actually submitted.
 *
 * A position naming a row that no longer exists is dropped rather than guessed at: the creator has edited since
 * the refusal, and pinning the message on whichever row moved into that slot would blame the wrong line.
 */
export function messagesByRowKey(
  fieldErrors: Readonly<Record<string, readonly string[]>>,
  list: 'ingredientGroups' | 'instructions',
  submitted: readonly (readonly string[])[],
): ReadonlyMap<string, readonly string[]> {
  const byRow = new Map<string, string[]>();

  for (const [key, messages] of Object.entries(fieldErrors)) {
    const position = parseErrorPosition(key);
    if (position === null || position.list !== list || position.lineIndex === null) continue;

    const rowKey = submitted[position.groupIndex]?.[position.lineIndex];
    if (rowKey === undefined) continue;

    const existing = byRow.get(rowKey);
    if (existing) existing.push(...messages);
    else byRow.set(rowKey, [...messages]);
  }

  return byRow;
}

/** The same, for a message about a group rather than a row inside it. */
export function messagesByGroupKey(
  fieldErrors: Readonly<Record<string, readonly string[]>>,
  list: 'ingredientGroups' | 'instructions',
  submittedGroups: readonly string[],
): ReadonlyMap<string, readonly string[]> {
  const byGroup = new Map<string, string[]>();

  for (const [key, messages] of Object.entries(fieldErrors)) {
    const position = parseErrorPosition(key);
    if (position === null || position.list !== list || position.lineIndex !== null) continue;

    const groupKey = submittedGroups[position.groupIndex];
    if (groupKey === undefined) continue;

    const existing = byGroup.get(groupKey);
    if (existing) existing.push(...messages);
    else byGroup.set(groupKey, [...messages]);
  }

  return byGroup;
}
