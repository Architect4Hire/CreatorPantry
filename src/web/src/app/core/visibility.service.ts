import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** How far outside the viewport an element starts counting as near it, so work begins just before it shows. */
const NEAR_VIEWPORT_MARGIN = '200px';

/**
 * Telling a component when its element is about to be seen.
 *
 * A service so a component asks one question — "is this near the screen yet?" — and a test can answer it
 * without scrolling a real viewport.
 *
 * **It answers once.** The stream emits the first time the element comes near the viewport and completes; a
 * caller that started work then has no reason to hear about it again.
 *
 * **It never withholds the answer.** A browser without `IntersectionObserver` gets an immediate "yes", because
 * work done early is better than work never done.
 */
@Injectable({ providedIn: 'root' })
export class VisibilityService {
  whenNearViewport(element: Element): Observable<void> {
    return new Observable<void>((subscriber) => {
      if (typeof IntersectionObserver === 'undefined') {
        subscriber.next();
        subscriber.complete();
        return undefined;
      }

      const observer = new IntersectionObserver(
        (entries) => {
          if (entries.some((entry) => entry.isIntersecting)) {
            subscriber.next();
            subscriber.complete();
          }
        },
        { rootMargin: NEAR_VIEWPORT_MARGIN },
      );
      observer.observe(element);

      return () => observer.disconnect();
    });
  }
}
