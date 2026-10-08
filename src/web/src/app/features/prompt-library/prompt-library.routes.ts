import { Routes } from '@angular/router';

/**
 * The Prompt Library's routes: the list at its index and one prompt beside it.
 *
 * No `canDeactivate` guard on either. A saved prompt is immutable and neither page holds an edit, so leaving
 * loses nothing.
 */
export const PROMPT_LIBRARY_ROUTES: Routes = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./prompt-library.component').then((m) => m.PromptLibraryComponent), data: { title: 'Prompt Library' } },
  { path: ':promptRecordId', loadComponent: () => import('./prompt-detail.component').then((m) => m.PromptDetailComponent), data: { title: 'Prompt' } },
];
