import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import {
  CP_GENERATED_LABELS,
  CpGeneratedComponent,
  CpGeneratedKind,
  CpGeneratedLabelComponent,
} from './generated.component';

@Component({
  imports: [CpGeneratedComponent, CpGeneratedLabelComponent],
  template: `<div style="width: 320px">
      <cp-generated [kind]="kind()" [provenance]="provenance()">
        <p class="output">Brown butter, toasted pecans, a little flaky salt.</p>
        <!-- One @if each: a block with two roots is projected to the default slot, not by its selector. -->
        @if (actions()) {
          <button cpGeneratedActions type="button" class="accept">Accept</button>
        }
        @if (actions()) {
          <button cpGeneratedActions type="button" class="discard">Discard</button>
        }
      </cp-generated>
    </div>
    <p class="beside">Instagram <cp-generated-label [kind]="kind()" [provenance]="provenance()" /></p>`,
})
class HostComponent {
  readonly kind = signal<CpGeneratedKind>('words');
  readonly provenance = signal('');
  readonly actions = signal(false);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because :has() and display are only computed for an element in the document.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const surface = (): HTMLElement => el.querySelector('cp-generated')!;
const alone = (): HTMLElement => el.querySelector('.beside cp-generated-label')!;
const words = (root: HTMLElement): string => root.querySelector('.words')!.textContent!.trim();

describe('CpGeneratedLabelComponent', () => {
  afterEach(() => el?.remove());

  it('says what made it in words, beside a glyph a screen reader does not hear', () => {
    mount();
    expect(words(alone())).toBe('Drafted by AI');
    const glyph = alone().querySelector('.glyph')!;
    expect(glyph.textContent).toBe('✦');
    expect(glyph.getAttribute('aria-hidden')).toBe('true');
  });

  it('takes its words from the kind, never from free text', () => {
    mount();
    for (const kind of ['words', 'picture', 'reading'] as const) {
      host.kind.set(kind);
      fixture.detectChanges();
      expect(words(alone())).withContext(kind).toBe(CP_GENERATED_LABELS[kind]);
    }
    expect(CP_GENERATED_LABELS).toEqual({ words: 'Drafted by AI', picture: 'Made by AI', reading: 'Read by AI' });
  });

  it('shows provenance only when the feature gives some, as the feature wrote it', () => {
    mount();
    expect(alone().querySelector('.provenance')).toBeNull();

    host.provenance.set('Venice, flux-dev');
    fixture.detectChanges();
    expect(alone().querySelector('.provenance')?.textContent?.trim()).toBe('Venice, flux-dev');
    // A pause a screen reader hears between the label and what made it, where a sighted reader sees the edge.
    expect(alone().querySelector('.cp-sr-only')?.textContent).toBe(', ');
  });

  it('draws a neutral dashed edge, not a tone', () => {
    mount();
    const page = document.createElement('div');
    page.setAttribute('data-cp-tone', 'pink');
    page.appendChild(el);
    document.body.appendChild(page);

    const mark = getComputedStyle(alone().querySelector('.mark')!);
    const probe = document.createElement('span');
    probe.style.color = 'var(--cp-border-control)';
    page.appendChild(probe);
    expect(mark.borderTopStyle).toBe('dashed');
    expect(mark.borderTopColor).toBe(getComputedStyle(probe).color);
    page.remove();
  });
});

describe('CpGeneratedComponent', () => {
  afterEach(() => el?.remove());

  it('is a group named by its label, so the label is heard before the output', () => {
    mount((h) => h.kind.set('picture'));
    expect(surface().getAttribute('role')).toBe('group');
    // The name is the words alone: the glyph is outside the element the group is labelled by.
    const named = document.getElementById(surface().getAttribute('aria-labelledby')!)!;
    expect(named.textContent).toBe('Made by AI');
    expect(surface().contains(named)).toBeTrue();
  });

  it('gives each surface its own name', () => {
    mount();
    const second = TestBed.createComponent(HostComponent);
    document.body.appendChild(second.nativeElement);
    second.detectChanges();
    const other = second.nativeElement.querySelector('cp-generated') as HTMLElement;
    expect(other.getAttribute('aria-labelledby')).not.toBe(surface().getAttribute('aria-labelledby'));
    second.nativeElement.remove();
  });

  it('reads label, provenance, output, then the creator’s decisions', () => {
    mount((h) => {
      h.provenance.set('gpt-4o · 25 Oct, 08:30');
      h.actions.set(true);
    });
    const order = Array.from(surface().querySelectorAll('.words, .provenance, .output, .accept, .discard')).map(
      (n) => n.className,
    );
    expect(order).toEqual(['words', 'provenance', 'output', 'accept', 'discard']);
  });

  it('gives the actions row no room when there are none', () => {
    mount();
    expect(getComputedStyle(surface().querySelector('.actions')!).display).toBe('none');

    host.actions.set(true);
    fixture.detectChanges();
    expect(getComputedStyle(surface().querySelector('.actions')!).display).not.toBe('none');
  });

  it('edges the surface dashed in the control colour, with no fill of its own', () => {
    mount();
    const style = getComputedStyle(surface());
    expect(style.borderTopStyle).toBe('dashed');
    // A hairline at any pixel ratio: one CSS pixel can compute to a fraction of one.
    expect(parseFloat(style.borderTopWidth)).toBeGreaterThan(0);
    expect(parseFloat(style.borderTopWidth)).toBeLessThanOrEqual(1);
    expect(style.backgroundColor).toBe('rgba(0, 0, 0, 0)');
  });

  // AU.3.4: what a model made arrives as it is made, so the surface it arrives on carries the entrance.
  it('arrives, fading and lifting in, when the result renders', () => {
    // Its own mount: reading the last test's element made this pass or fail by the random order.
    mount();
    const surface = el.querySelector('cp-generated') as HTMLElement;

    expect(surface.hasAttribute('data-cp-enter')).toBeTrue();
  });
});
