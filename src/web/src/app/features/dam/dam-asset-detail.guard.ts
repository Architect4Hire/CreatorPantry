import { CanDeactivateFn } from '@angular/router';

import { DamAssetDetailComponent } from './dam-asset-detail.component';

/**
 * Router-driven navigation away from an asset's page is confirmed through the component itself, which knows
 * whether an edit is unsaved or an upload is running. A full browser navigation, close or refresh is not
 * covered by this guard — that is the separate `beforeunload` listener in the component, since the Router
 * never sees those.
 */
export const damAssetDetailCanDeactivateGuard: CanDeactivateFn<DamAssetDetailComponent> = (component) =>
  component.confirmLeave();
