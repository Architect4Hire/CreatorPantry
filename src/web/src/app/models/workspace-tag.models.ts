// The workspace's own tag vocabulary, as a picker reads it. Mirrors `WorkspaceTagServiceModel`.

import { isRecord } from './recipe.models';

/** One tag: an id to send and the creator's own spelling to show. */
export interface WorkspaceTag {
  readonly id: string;
  readonly name: string;
}

/**
 * The list the route answers with, or null when it is not one.
 *
 * One unreadable entry fails the list rather than silently shortening it: a picker missing a tag with no sign
 * of why is worse than a picker that says the tags could not be loaded.
 */
export function decodeWorkspaceTags(value: unknown): WorkspaceTag[] | null {
  if (!Array.isArray(value)) return null;

  const tags: WorkspaceTag[] = [];
  for (const item of value) {
    if (!isRecord(item)) return null;

    const { id, name } = item;
    if (typeof id !== 'string' || id === '' || typeof name !== 'string') return null;

    tags.push({ id, name });
  }

  return tags;
}
