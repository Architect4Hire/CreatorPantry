import { Routes } from '@angular/router';

/**
 * The content board's routes: the board at its index and one card beside it.
 *
 * The board asks for the wide frame: it sets four columns side by side. A card's page reads as one column and
 * takes the standard one.
 *
 * No `canDeactivate` guard on either. The board holds no unsaved edit of its own — a move is sent as it is
 * made — and both forms that do hold one, the new-card dialog and the edit drawer, ask before they are closed.
 */
export const CONTENT_BOARD_ROUTES: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('./content-board.component').then((m) => m.ContentBoardComponent),
    data: { title: 'Content Board', pageWidth: 'wide' },
  },
  {
    path: ':cardId',
    loadComponent: () => import('./content-card-detail.component').then((m) => m.ContentCardDetailComponent),
    data: { title: 'Card' },
  },
];
