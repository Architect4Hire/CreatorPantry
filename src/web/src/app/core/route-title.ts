import { inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

/**
 * The `title` a routed page's own route declares, for the page to print as its `cp-page-head` heading.
 *
 * One source for a page's name: `PageTitleStrategy` puts the same string in the browser tab. A page whose
 * title is a record's own name — a recipe, a picture — does not use this.
 */
export function injectRouteTitle(fallback = 'CreatorPantry'): string {
  // Optional: a page mounted with no router at all — a spec, a showcase — still has a name to print.
  const title: unknown = inject(ActivatedRoute, { optional: true })?.snapshot?.data?.['title'];
  return typeof title === 'string' && title !== '' ? title : fallback;
}
