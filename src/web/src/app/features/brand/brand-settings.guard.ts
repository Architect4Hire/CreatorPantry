import { CanDeactivateFn } from '@angular/router';

import { BrandSettingsComponent } from './brand-settings.component';

/**
 * Router-driven navigation away from the brand settings is confirmed through the component, which owns the
 * dirty state and the confirm dialog. A full browser close or refresh is the component's `beforeunload`
 * listener, which the Router never sees.
 */
export const brandSettingsCanDeactivateGuard: CanDeactivateFn<BrandSettingsComponent> = (component) =>
  component.confirmDiscardIfDirty();
