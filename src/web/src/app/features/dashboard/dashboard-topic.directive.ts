import { Directive, TemplateRef, inject, input } from '@angular/core';

/** Every topic the dashboard draws, in the order it draws them when none has been lifted out. */
export const DASHBOARD_TOPIC_KEYS = [
  'continue',
  'stuck',
  'day',
  'week',
  'publishing',
  'readiness',
  'tests',
  'proposals',
  'upcoming',
  'recipes',
  'board',
  'uploads',
] as const;

export type DashboardTopicKey = (typeof DASHBOARD_TOPIC_KEYS)[number];

/**
 * One topic's card, declared once and placed twice over: as the page's one featured card when it is the most
 * urgent, or as a cell of the grid when it is not (AU.4.1).
 *
 * The page cannot hold two copies of a topic — the same heading and the same links in two places would be two
 * destinations for one thing — so each topic is an `ng-template` the page points at from whichever place it
 * belongs in. The order the grid draws them in is the order they are declared, which is the order
 * `DESIGN-SYSTEM.md` ("A dashboard") asks for: what needs the creator now, then what is worth a glance.
 */
@Directive({
  selector: 'ng-template[cpDashboardTopic]',
  standalone: true,
})
export class DashboardTopicDirective {
  readonly key = input.required<DashboardTopicKey>({ alias: 'cpDashboardTopic' });
  readonly body = inject<TemplateRef<void>>(TemplateRef);
}
