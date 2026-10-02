import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpNoticeComponent, CpNoticeTone } from './notice.component';

@Component({
  imports: [CpNoticeComponent],
  template: `<cp-notice [tone]="tone()" [quiet]="quiet()" [glyph]="glyph()" role="status">
    Saved at 4:05pm.
    <button type="button">Undo</button>
  </cp-notice>`,
})
class HostComponent {
  readonly tone = signal<CpNoticeTone>('neutral');
  readonly quiet = signal(false);
  readonly glyph = signal('');
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  fixture.detectChanges();
}

const notice = (): HTMLElement => el.querySelector('cp-notice')!;
const glyph = (): HTMLElement | null => el.querySelector('.glyph');

describe('CpNoticeComponent', () => {
  it('shows what it was given, and leaves liveness to the consumer', () => {
    mount();
    expect(notice().textContent).toContain('Saved at 4:05pm.');
    // The consumer's own role, untouched: only the consumer knows whether this sentence interrupts.
    expect(notice().getAttribute('role')).toBe('status');
    expect(notice().getAttribute('aria-live')).toBeNull();
  });

  it('carries a glyph for every tone, so meaning never rests on the fill alone', () => {
    for (const tone of ['neutral', 'success', 'warning', 'error'] as const) {
      TestBed.resetTestingModule();
      mount((h) => h.tone.set(tone));
      expect(glyph()?.textContent?.trim()).withContext(tone).toBeTruthy();
      expect(glyph()?.getAttribute('aria-hidden')).toBe('true');
    }
  });

  it('takes a tone class per tone and none for the default', () => {
    mount();
    expect(notice().className).toBe('');

    for (const [tone, expected] of [
      ['success', 'cp-notice--success'],
      ['warning', 'cp-notice--warning'],
      ['error', 'cp-notice--error'],
    ] as const) {
      TestBed.resetTestingModule();
      mount((h) => h.tone.set(tone));
      expect(notice().classList).toContain(expected);
    }
  });

  it('lets a consumer replace the glyph but not blank it', () => {
    mount((h) => h.glyph.set('⚑'));
    expect(glyph()?.textContent?.trim()).toBe('⚑');

    TestBed.resetTestingModule();
    mount((h) => h.glyph.set(''));
    expect(glyph()?.textContent?.trim()).toBeTruthy();
  });

  it('holds the space without drawing anything when quiet', () => {
    mount((h) => h.quiet.set(true));
    expect(notice().classList).toContain('cp-notice--quiet');
    // No glyph: an empty live region waiting for text should leave no mark.
    expect(glyph()).toBeNull();
    expect(getComputedStyle(notice()).paddingTop).toBe('0px');
  });

  it('keeps a tone off the element while it is quiet', () => {
    mount((h) => {
      h.quiet.set(true);
      h.tone.set('error');
    });
    expect(notice().classList).not.toContain('cp-notice--error');
    expect(notice().classList).toContain('cp-notice--quiet');
  });

  it('keeps a projected action inside it, where the sentence it belongs to is', () => {
    mount();
    const action = notice().querySelector('button');
    expect(action?.textContent?.trim()).toBe('Undo');
  });
});
