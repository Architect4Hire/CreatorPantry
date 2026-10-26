import { DestroyRef, Signal, inject, signal } from '@angular/core';

import { WORKFLOW_SHELL_NARROW_QUERY } from './workflow-shell.component';

/**
 * Whether a flow is narrow enough to fold its stepper into a disclosure, as a workflow run's is
 * (`WORKFLOW_SHELL_NARROW_QUERY`): the width the page gutter drops at, and 200% zoom of a 1280px window. Kept in
 * step with the window for the life of the component that asks, so a flow folding its steps uses the run's width
 * and not one of its own.
 */
export function injectFlowNarrow(): Signal<boolean> {
  const media = typeof window === 'undefined' ? undefined : window.matchMedia?.(WORKFLOW_SHELL_NARROW_QUERY);
  const narrow = signal(media?.matches === true);

  if (media) {
    const update = (event: MediaQueryListEvent): void => narrow.set(event.matches);
    media.addEventListener('change', update);
    inject(DestroyRef).onDestroy(() => media.removeEventListener('change', update));
  }

  return narrow.asReadonly();
}
