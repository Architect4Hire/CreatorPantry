import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpFactComponent, CpFactsComponent, CpFactsLayout } from './facts.component';

@Component({
  imports: [CpFactsComponent, CpFactComponent],
  template: `<dl cpFacts [layout]="layout()">
    <div cpFact label="File">JPEG · 2.4 MB</div>
    <div cpFact label="Notes" prose>{{ notes }}</div>
    <div cpFact label="Tags">
      <ul>
        <li>bread</li>
        <li>autumn</li>
      </ul>
    </div>
  </dl>`,
})
class HostComponent {
  readonly layout = signal<CpFactsLayout>('stacked');
  readonly notes = 'Shot at noon.\nNo flash.';
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because a detached element has no computed layout.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const fact = (index: number): HTMLElement => el.querySelectorAll<HTMLElement>('dl > div')[index];

describe('CpFactsComponent', () => {
  beforeEach(() => {
    document.documentElement.style.setProperty('--cp-font-size-xs', '11px');
    document.documentElement.style.setProperty('--cp-font-size-sm', '13px');
  });

  afterEach(() => {
    document.documentElement.style.removeProperty('--cp-font-size-xs');
    document.documentElement.style.removeProperty('--cp-font-size-sm');
    el?.remove();
  });

  it('stays a real description list: a dl of divs, each a term and its value', () => {
    mount();
    const list = el.querySelector('dl')!;
    // Nothing but divs directly inside the list, and nothing but a dt and a dd directly inside each.
    expect(Array.from(list.children).map((child) => child.tagName)).toEqual(['DIV', 'DIV', 'DIV']);
    for (const each of Array.from(list.children)) {
      expect(Array.from(each.children).map((child) => child.tagName)).toEqual(['DT', 'DD']);
    }
    expect(fact(0).querySelector('dt')?.textContent).toBe('File');
    expect(fact(0).querySelector('dd')?.textContent).toBe('JPEG · 2.4 MB');
  });

  it('takes any value by projection, a list included', () => {
    mount();
    expect(Array.from(fact(2).querySelectorAll('dd li')).map((li) => li.textContent)).toEqual(['bread', 'autumn']);
  });

  it("keeps a prose value's own line breaks, and only a prose value's", () => {
    mount();
    expect(getComputedStyle(fact(1).querySelector('dd')!).whiteSpace).toBe('pre-wrap');
    expect(getComputedStyle(fact(0).querySelector('dd')!).whiteSpace).toBe('normal');
  });

  it('stacks a small term over its value by default', () => {
    mount();
    expect(el.querySelector('dl')?.getAttribute('data-layout')).toBe('stacked');
    expect(getComputedStyle(fact(0)).display).toBe('block');
    expect(getComputedStyle(fact(0).querySelector('dt')!).fontSize).toBe('11px');

    const term = fact(0).querySelector('dt')!.getBoundingClientRect();
    const value = fact(0).querySelector('dd')!.getBoundingClientRect();
    // Rounded: adjacent boxes can differ by a fraction of a pixel after layout.
    expect(Math.round(value.top)).toBeGreaterThanOrEqual(Math.round(term.bottom));
  });

  it('puts term and value on one line when inline, at one size', () => {
    mount((h) => h.layout.set('inline'));
    expect(getComputedStyle(fact(0)).display).toBe('flex');
    expect(getComputedStyle(fact(0).querySelector('dt')!).fontSize).toBe('13px');
    expect(getComputedStyle(fact(0).querySelector('dd')!).fontSize).toBe('13px');

    const term = fact(0).querySelector('dt')!.getBoundingClientRect();
    const value = fact(0).querySelector('dd')!.getBoundingClientRect();
    expect(Math.round(value.left)).toBeGreaterThanOrEqual(Math.round(term.right));
  });

  it('sets the facts side by side when across, each a small term over a bold value, wrapping when out of room', () => {
    mount((h) => h.layout.set('across'));
    const list = el.querySelector('dl')!;
    expect(getComputedStyle(list).display).toBe('flex');
    expect(getComputedStyle(list).flexWrap).toBe('wrap');

    const first = fact(0).getBoundingClientRect();
    const second = fact(1).getBoundingClientRect();
    expect(Math.round(second.left)).toBeGreaterThan(Math.round(first.right));
    expect(Math.round(second.top)).toBe(Math.round(first.top));

    const term = fact(0).querySelector('dt')!;
    const value = fact(0).querySelector('dd')!;
    expect(getComputedStyle(term).fontSize).toBe('11px');
    expect(Math.round(value.getBoundingClientRect().top)).toBeGreaterThanOrEqual(
      Math.round(term.getBoundingClientRect().bottom),
    );
    expect(getComputedStyle(value).fontWeight).toBe('600');
  });
});
