import { CanDeactivateFn } from '@angular/router';

import { BrandSetupShellComponent } from './brand-setup-shell.component';

/** `/ws/brand/setup/goals?x` -> `/ws/brand/setup`, so a move between steps is recognisably not a departure. */
function setupBase(url: string): string {
  const path = url.split(/[?#]/)[0];
  const at = path.indexOf('/setup');
  return at < 0 ? path : path.slice(0, at + '/setup'.length);
}

/**
 * Router-driven navigation out of the wizard is confirmed through the component, which owns the unsaved-edit
 * state. Moving between steps re-runs this guard (the `:step` param changes) and is always allowed. A full
 * browser close or refresh is the component's `beforeunload` listener, which the Router never sees.
 */
export const brandSetupCanDeactivateGuard: CanDeactivateFn<BrandSetupShellComponent> = (
  component,
  _route,
  currentState,
  nextState,
) => {
  const base = setupBase(currentState.url);
  const next = nextState.url.split(/[?#]/)[0];
  if (next === base || next.startsWith(`${base}/`)) return true;
  return component.confirmLeaveIfUnsaved();
};
