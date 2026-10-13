import { CanDeactivateFn } from '@angular/router';

import type { SocialStudioComponent } from './social-studio.component';

/**
 * Router-driven navigation away from Social Studio is confirmed through the page, which knows whether a post
 * has words nobody has saved. A full browser close or refresh is the page's `beforeunload` listener, which the
 * Router never sees.
 */
export const socialStudioCanDeactivateGuard: CanDeactivateFn<SocialStudioComponent> = (component) =>
  component.confirmLeaveWithUnsavedWords();
