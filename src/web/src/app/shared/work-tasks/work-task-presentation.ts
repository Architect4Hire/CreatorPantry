// How a work task is said and where it leads, shared by My Day (13A.32) and My Week (13A.33). Words and addresses
// only: nothing here decides, sorts or counts.

import { WorkTask } from '../../models/work-task.models';

export interface WorkTaskLink {
  readonly commands: readonly string[];
  /** What it opens, as a noun: "the workflow", "the recipe". */
  readonly what: string;
}

/**
 * Where a task's work is done, from the record it points at: the workflow step it came from first, because that is
 * where the work resumes, then the recipe, the card, the picture. Null for a task about nothing in particular.
 */
export function linkFor(task: WorkTask, workspaceSlug: string): WorkTaskLink | null {
  const { links } = task;
  const route = links.workflowRunRoute;

  // A relative address the server built from an id; anything else is not followed.
  if (route && /^workflows\/runs\/[0-9a-f-]{36}$/i.test(route)) {
    return { commands: ['/', workspaceSlug, ...route.split('/')], what: 'the workflow' };
  }
  if (links.recipeId) return { commands: ['/', workspaceSlug, 'recipes', links.recipeId], what: 'the recipe' };
  if (links.contentCardId) return { commands: ['/', workspaceSlug, 'content-board', links.contentCardId], what: 'the card' };
  if (links.mediaAssetId) return { commands: ['/', workspaceSlug, 'dam', links.mediaAssetId], what: 'the picture' };

  return null;
}

/** "Wed 14 Oct" — a calendar date as words, read as a date and never shifted by the browser's zone. */
export function shortDay(iso: string): string {
  return new Date(`${iso}T00:00:00Z`).toLocaleDateString('en-GB', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  });
}

/** "Friday 16 October" — the day the page is about. */
export function longDay(iso: string): string {
  return new Date(`${iso}T00:00:00Z`).toLocaleDateString('en-GB', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    timeZone: 'UTC',
  });
}

/** "30 min", "1 hr", "1 hr 30 min" — an estimate in words. Null is unknown and says nothing. */
export function estimateWords(minutes: number | null): string | null {
  if (minutes === null) return null;

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours === 0) return `${rest} min`;

  return rest === 0 ? `${hours} hr` : `${hours} hr ${rest} min`;
}
