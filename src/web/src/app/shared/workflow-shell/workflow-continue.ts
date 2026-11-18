/** The navigation-state key that says which run a Continue was pressed on. */
export const CONTINUE_RUN_STATE = 'cpContinueRun';

/**
 * Router navigation state for a link that continues a run — a Continue the creator has already pressed. The shell
 * then opens straight on the step rather than asking "Pick up where you left off" again. Arriving any other way (an
 * address typed or bookmarked, a link from elsewhere) still shows the resume card. Navigation state, not the
 * address, so the choice is never shared or bookmarked with the link.
 */
export function continueRunState(runId: string): Record<string, string> {
  return { [CONTINUE_RUN_STATE]: runId };
}
