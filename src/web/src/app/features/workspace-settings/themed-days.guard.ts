import { CanDeactivateFn } from '@angular/router';

import type { ThemedDaysComponent } from './themed-days.component';

/**
 * Router-driven navigation away from the themed days is confirmed through the component, which owns the dirty
 * state and the confirm dialog. A full browser close or refresh is the component's `beforeunload` listener,
 * which the Router never sees.
 */
export const themedDaysCanDeactivateGuard: CanDeactivateFn<ThemedDaysComponent> = (component) =>
  component.confirmDiscardIfDirty();
