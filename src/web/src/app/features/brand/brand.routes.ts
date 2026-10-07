import { Routes } from '@angular/router';

import { brandGuideEditorCanDeactivateGuard } from './brand-guide-editor.guard';
import { brandSettingsCanDeactivateGuard } from './brand-settings.guard';
import { brandSetupCanDeactivateGuard } from './setup/brand-setup.guard';

export const BRAND_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./brand-settings.component').then((m) => m.BrandSettingsComponent), canDeactivate: [brandSettingsCanDeactivateGuard], data: { title: 'Brand' } },
  // The examples this workspace has given the brand. Under 'brand' so the Brand nav item stays active.
  {
    path: 'library',
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./brand-library.component').then((m) => m.BrandLibraryComponent), data: { title: 'Brand library' } },
      { path: ':documentId', loadComponent: () => import('./brand-source-detail.component').then((m) => m.BrandSourceDetailComponent), data: { title: 'Brand example' } },
    ],
  },
  // One guide's version history, the comparison built from it, and the Owner's activation decision.
  // Under 'brand' for the same reason the library is: the Brand nav item stays active throughout.
  {
    path: 'style-guide',
    children: [
      { path: ':guideId', pathMatch: 'full', loadComponent: () => import('./brand-guide-editor.component').then((m) => m.BrandGuideEditorComponent), canDeactivate: [brandGuideEditorCanDeactivateGuard], data: { title: 'Edit voice guide' } },
      { path: ':guideId/history', loadComponent: () => import('./brand-guide-history.component').then((m) => m.BrandGuideHistoryComponent), data: { title: 'Guide history' } },
      // 11A.24. The version is a query parameter rather than a segment: it is the one thing a creator
      // changes without leaving, and the history links here per row.
      { path: ':guideId/test-drive', loadComponent: () => import('./brand-style-test-drive.component').then((m) => m.BrandStyleTestDriveComponent), data: { title: 'Test my style' } },
    ],
  },
  // The "Create my voice" wizard. Everything under 'brand' keeps the Brand nav item active
  // (RouterLinkActive matches the subtree), so the section never appears to be left mid-setup.
  {
    path: 'setup',
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'goals' },
      { path: ':step', loadComponent: () => import('./setup/brand-setup-shell.component').then((m) => m.BrandSetupShellComponent), canDeactivate: [brandSetupCanDeactivateGuard], data: { title: 'Create my voice' } },
    ],
  },
];
