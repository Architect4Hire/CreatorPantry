/**
 * One thing a form has to say about one place in it.
 *
 * The same shape carries a rule this client checked and a field error the server refused the save with, so a
 * field shows one stream of messages whatever produced them and a component never asks where one came from.
 *
 * - `error` stops a save. `warning` and `info` never do: they say something the creator may want to know —
 *   a total that disagrees with its parts — and leave the decision to them.
 * - `path` is the server's own key syntax: `title`, `ingredientGroups[0].ingredients[2].quantity`. That is what
 *   lets a refusal land on the field it names without a translation table.
 * - `code` is stable and machine-readable (`required`, `maxLength`); `message` is the sentence the creator reads.
 */
export type IssueSeverity = 'error' | 'warning' | 'info';

/** Where an issue came from. Only a `client` error blocks a save; a `server` one is the last save's answer. */
export type IssueSource = 'client' | 'server';

export interface FormIssue {
  readonly path: string;
  readonly severity: IssueSeverity;
  readonly code: string;
  readonly message: string;
  readonly source: IssueSource;
}

/** The code a server field error carries: the wire gives a sentence per field, not a rule name. */
export const SERVER_ISSUE_CODE = 'server';

/**
 * Whether `path` is `prefix` or lies inside it.
 *
 * `ingredientGroups[0].ingredients[2].quantity` and `ingredientGroups` are the same tab's business, so a count
 * for a tab or a section reads a path through this rather than matching it exactly. `ingredientGroupsExtra` is
 * not inside `ingredientGroups`.
 */
export function isPathUnder(path: string, prefix: string): boolean {
  if (path === prefix) return true;
  if (!path.startsWith(prefix)) return false;

  const next = path.charAt(prefix.length);

  return next === '.' || next === '[';
}
