import { Routes } from '@angular/router';

import { damAssetDetailCanDeactivateGuard } from './dam-asset-detail.guard';

/**
 * The DAM's routes: the library at its index and one asset beside it.
 *
 * The library is a read, so leaving it loses nothing. An asset's page can hold an unsaved edit or a running
 * upload, so leaving that one is confirmed.
 */
export const DAM_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./dam-library.component').then((m) => m.DamLibraryComponent), data: { title: 'DAM' } },
  { path: ':assetId', loadComponent: () => import('./dam-asset-detail.component').then((m) => m.DamAssetDetailComponent), canDeactivate: [damAssetDetailCanDeactivateGuard], data: { title: 'Asset' } },
];
