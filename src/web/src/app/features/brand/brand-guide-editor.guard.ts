import { CanDeactivateFn } from '@angular/router';

import { BrandGuideEditorComponent } from './brand-guide-editor.component';

/**
 * Asks before leaving the guide editor with changes that are not part of the guide yet.
 *
 * Autosave keeps them, so nothing is lost by navigating — but "kept" and "saved as a version" are different
 * things, and a creator who meant to save one should not discover the difference later. The browser's own
 * close and reload cannot be confirmed by any of this; that is the browser's dialog, and a kept draft is what
 * makes it survivable either way.
 */
export const brandGuideEditorCanDeactivateGuard: CanDeactivateFn<BrandGuideEditorComponent> = (component) =>
  !component.hasUnsavedChanges() ||
  window.confirm(
    'Your changes are kept as a draft, but they are not part of this guide until you save a new version. Leave anyway?',
  );
