import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LandingRevealDirective, REVEAL_MOTION_QUERY } from './landing-reveal.directive';

@Component({
  standalone: true,
  imports: [LandingRevealDirective],
  template: `<section [cpReveal]="selector">
    <ul>
      <li>one</li>
      <li>two</li>
      <li>three</li>
    </ul>
  </section>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
class HostComponent {
  selector = '';
}

describe('LandingRevealDirective', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HTMLElement;
  let observed: { element: Element; fire: (isIntersecting: boolean) => void }[];
  let realObserver: typeof IntersectionObserver;

  /** Stands in for the browser's, so a spec can say exactly when a section comes into view. */
  function stubObserver(): void {
    observed = [];
    realObserver = window.IntersectionObserver;
    (window as unknown as { IntersectionObserver: unknown }).IntersectionObserver = class {
      constructor(private readonly callback: IntersectionObserverCallback) {}
      observe(element: Element): void {
        observed.push({
          element,
          fire: (isIntersecting) =>
            this.callback([{ isIntersecting, target: element } as IntersectionObserverEntry], this as never),
        });
      }
      disconnect(): void {}
      unobserve(): void {}
    };
  }

  async function create(motionAllowed: boolean, selector = ''): Promise<void> {
    const real = window.matchMedia.bind(window);
    spyOn(window, 'matchMedia').and.callFake((query: string) =>
      query === REVEAL_MOTION_QUERY ? ({ matches: motionAllowed, media: query } as MediaQueryList) : real(query),
    );

    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.selector = selector;
    host = fixture.nativeElement.querySelector('section')!;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const items = () => Array.from(host.querySelectorAll<HTMLElement>('li'));

  afterEach(() => {
    if (realObserver) (window as unknown as { IntersectionObserver: unknown }).IntersectionObserver = realObserver;
    fixture?.destroy();
  });

  /**
   * The whole safety property: nothing is hidden by a stylesheet, so every way this can fail to run leaves a
   * section that was never hidden. A reveal built the usual way round turns reduced motion into a blank page.
   */
  it('touches nothing at all when the visitor has asked for reduced motion', async () => {
    stubObserver();
    await create(false, 'li');

    expect(host.getAttribute('style')).toBeNull();
    items().forEach((item) => expect(item.getAttribute('style')).toBeNull());
    expect(observed.length).toBe(0);
  });

  it('starts a section hidden and brings it in when it is seen', async () => {
    stubObserver();
    await create(true);

    expect(host.style.opacity).toBe('0');
    expect(host.style.translate).toBe('0 var(--cp-space-2)');
    expect(observed.length).toBe(1);

    observed[0].fire(true);

    expect(host.style.opacity).toBe('1');
    // The browser collapses a two-axis zero to one value, so this reads back shorter than it was written.
    expect(host.style.translate).toBe('0px');
  });

  it('stays hidden while the section has not been reached', async () => {
    stubObserver();
    await create(true);

    observed[0].fire(false);

    expect(host.style.opacity).toBe('0');
  });

  it('brings named children in one behind the other', async () => {
    stubObserver();
    await create(true, 'li');

    const delays = items().map((item) => item.style.transition.match(/translate[^,]*?(\d+)ms/)?.[1]);
    expect(delays).toEqual(['0', '60', '120']);

    observed[0].fire(true);
    items().forEach((item) => expect(item.style.opacity).toBe('1'));
  });

  /**
   * A print and a full-page capture both render the whole document without scrolling it, so a section still
   * waiting to be reached would come out as blank space.
   */
  it('shows everything that has not arrived yet when the page is about to be printed', async () => {
    stubObserver();
    await create(true, 'li');

    expect(host.style.opacity).toBe('0');

    window.dispatchEvent(new Event('beforeprint'));

    expect(host.style.opacity).toBe('1');
    items().forEach((item) => expect(item.style.opacity).toBe('1'));
  });

  /** A browser with no observer would otherwise be left looking at a section hidden for good. */
  it('shows the section when there is nothing to watch the scroll with', async () => {
    realObserver = window.IntersectionObserver;
    delete (window as unknown as { IntersectionObserver?: unknown }).IntersectionObserver;
    await create(true, 'li');

    expect(host.style.opacity).toBe('1');
    items().forEach((item) => expect(item.style.opacity).toBe('1'));
  });
});
