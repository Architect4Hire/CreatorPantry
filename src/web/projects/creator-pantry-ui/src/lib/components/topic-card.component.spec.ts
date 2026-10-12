import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpTopicCardComponent, CpTopicCardHeadingLevel } from './topic-card.component';

@Component({
  imports: [CpTopicCardComponent],
  template: `<div style="width: 1600px">
    <cp-topic-card [heading]="heading()" [headingLevel]="level()" [headingId]="headingId()" [wide]="wide()">
      <p class="body">Every version of this picture, newest first.</p>
      @if (action()) {
        <button cpTopicCardActions type="button">Add a version</button>
      }
    </cp-topic-card>
    <cp-topic-card heading="Second" wide><p>Uses the bare attribute.</p></cp-topic-card>
  </div>`,
})
class HostComponent {
  readonly heading = signal('Versions');
  readonly level = signal<CpTopicCardHeadingLevel>(2);
  readonly headingId = signal('');
  readonly wide = signal(false);
  readonly action = signal(false);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because :has(), display and max-width are only computed for an element in the document.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const card = (index = 0): HTMLElement => el.querySelectorAll<HTMLElement>('cp-topic-card')[index];
const section = (index = 0): HTMLElement => card(index).querySelector('section')!;

describe('CpTopicCardComponent', () => {
  beforeEach(() => document.documentElement.style.setProperty('--cp-measure-prose', '600px'));

  afterEach(() => {
    document.documentElement.style.removeProperty('--cp-measure-prose');
    el?.remove();
  });

  it('is a card holding a region named by its own heading', () => {
    mount();
    const heading = card().querySelector('cp-card section h2')!;
    expect(heading.textContent).toBe('Versions');
    expect(heading.id).not.toBe('');
    expect(section().getAttribute('aria-labelledby')).toBe(heading.id);
  });

  it('gives each card a heading id of its own', () => {
    mount();
    const first = section(0).getAttribute('aria-labelledby');
    const second = section(1).getAttribute('aria-labelledby');
    expect(first).not.toBe(second);
    expect(el.querySelectorAll(`#${first}`).length).toBe(1);
  });

  it("takes the page's id for its heading, so something else can be labelled by it", () => {
    mount((h) => h.headingId.set('versions-heading'));
    expect(card().querySelector('h2')?.id).toBe('versions-heading');
    expect(section().getAttribute('aria-labelledby')).toBe('versions-heading');
  });

  it('heads the card at level 3 when asked', () => {
    mount((h) => h.level.set(3));
    expect(card().querySelector('h3')?.textContent).toBe('Versions');
    expect(card().querySelector('h2')).toBeNull();
    expect(section().getAttribute('aria-labelledby')).toBe(card().querySelector('h3')!.id);
  });

  it('holds the body to the reading measure, and lifts it when wide', () => {
    mount();
    expect(section().getBoundingClientRect().width).toBe(600);

    host.wide.set(true);
    fixture.detectChanges();
    expect(section().getBoundingClientRect().width).toBeGreaterThan(600);
  });

  it('takes a bare wide attribute as true', () => {
    mount();
    expect(card(1).classList).toContain('cp-topic-card--wide');
    expect(section(1).getBoundingClientRect().width).toBeGreaterThan(600);
  });

  it('puts the actions last, and gives them no room when there are none', () => {
    mount();
    expect(getComputedStyle(card().querySelector('.actions')!).display).toBe('none');

    host.action.set(true);
    fixture.detectChanges();
    expect(getComputedStyle(card().querySelector('.actions')!).display).toBe('flex');

    const order = Array.from(card().querySelectorAll('h2, .body, button')).map((node) => node.tagName);
    expect(order).toEqual(['H2', 'P', 'BUTTON']);
  });

  it('lets a flow move focus to its heading, which is a destination and never a tab stop', () => {
    mount();
    const heading = card().querySelector('h2') as HTMLElement;
    expect(heading.tabIndex).toBe(-1);

    fixture.debugElement.children[0].children[0].componentInstance.focusHeading();

    expect(document.activeElement).toBe(heading);
    // On :focus, since focus moved by script after a click is not keyboard focus to a browser.
    expect(getComputedStyle(heading).outlineStyle).toBe('solid');
  });

  it('keeps the heading focusable at either level', () => {
    mount((h) => h.level.set(3));
    expect((card().querySelector('h3') as HTMLElement).tabIndex).toBe(-1);
  });
});
