import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpLightboxComponent } from './lightbox.component';

@Component({
  imports: [CpLightboxComponent],
  template: `<button id="opener" type="button">View larger</button>
    <cp-lightbox [open]="open()" heading="Lead picture" [description]="description()" (closeRequested)="requests.push('asked')">
      <img id="picture" alt="Picture 1 of 1" width="8" height="8" />
      @if (caption()) {
        <p cpLightboxCaption id="caption">Web size</p>
      }
      @if (actions()) {
        <!-- One root, so the block projects into the slot its attribute names. -->
        <div cpLightboxActions>
          <button id="first" type="button">Previous</button>
          <button id="last" type="button" (keydown.escape)="$event.preventDefault()">Next</button>
        </div>
      }
    </cp-lightbox>`,
})
class HostComponent {
  readonly open = signal(false);
  readonly description = signal('');
  readonly caption = signal(false);
  readonly actions = signal(false);
  readonly requests: string[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  document.body.appendChild(el);
  fixture.detectChanges();
}

function openFromButton(): void {
  el.querySelector<HTMLElement>('#opener')!.focus();
  host.open.set(true);
  fixture.detectChanges();
}

const surface = (): HTMLElement | null => el.querySelector('[role="dialog"]');

function key(target: HTMLElement, name: string, shiftKey = false): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: name, shiftKey, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  fixture.detectChanges();

  return event;
}

describe('CpLightboxComponent', () => {
  afterEach(() => {
    fixture.destroy();
    el.remove();
  });

  it('renders nothing while closed', () => {
    mount();

    expect(surface()).toBeNull();
    expect(el.querySelector('#picture')).toBeNull();
  });

  it('is a modal dialog named by its heading and described by its description', () => {
    mount((h) => h.description.set('1024 × 1024 pixels'));
    openFromButton();

    const dialog = surface()!;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(document.getElementById(dialog.getAttribute('aria-labelledby')!)?.textContent).toBe('Lead picture');
    expect(document.getElementById(dialog.getAttribute('aria-describedby')!)?.textContent).toBe('1024 × 1024 pixels');
  });

  it('is described by nothing when there is no description', () => {
    mount();
    openFromButton();

    expect(surface()!.hasAttribute('aria-describedby')).toBeFalse();
  });

  it('shows the projected picture in the stage', () => {
    mount();
    openFromButton();

    expect(el.querySelector('.cp-lightbox-stage #picture')).not.toBeNull();
  });

  it('takes focus on open and gives it back to what opened it', () => {
    mount();
    openFromButton();

    expect(document.activeElement).toBe(surface());

    host.open.set(false);
    fixture.detectChanges();

    expect(document.activeElement).toBe(el.querySelector('#opener'));
  });

  it('takes focus again on a second open', () => {
    mount();
    openFromButton();
    host.open.set(false);
    fixture.detectChanges();
    openFromButton();

    expect(document.activeElement).toBe(surface());
  });

  it('asks to close on Escape, the close control and the scrim, and closes on none of them', () => {
    mount();
    openFromButton();

    expect(key(surface()!, 'Escape').defaultPrevented).toBeTrue();
    el.querySelector<HTMLElement>('button[aria-label="Close picture"]')!.click();
    el.querySelector<HTMLElement>('.scrim')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));

    expect(host.requests.length).toBe(3);
    expect(surface()).not.toBeNull();
  });

  it('does not ask to close when the press was on the picture', () => {
    mount();
    openFromButton();

    el.querySelector<HTMLElement>('#picture')!.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));

    expect(host.requests).toEqual([]);
  });

  it('leaves an Escape that something inside has already handled', () => {
    mount((h) => h.actions.set(true));
    openFromButton();

    key(el.querySelector<HTMLElement>('#last')!, 'Escape');

    expect(host.requests).toEqual([]);
  });

  it('keeps Tab inside, wrapping at both ends', () => {
    mount((h) => h.actions.set(true));
    openFromButton();
    const close = el.querySelector<HTMLElement>('button[aria-label="Close picture"]')!;
    const last = el.querySelector<HTMLElement>('#last')!;

    last.focus();
    expect(key(last, 'Tab').defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(close);

    expect(key(close, 'Tab', true).defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(last);
  });

  it('stops the page scrolling while open and puts it back after', () => {
    mount();
    const before = document.body.style.overflow;
    openFromButton();

    expect(document.body.style.overflow).toBe('hidden');

    host.open.set(false);
    fixture.detectChanges();

    expect(document.body.style.overflow).toBe(before);
  });

  it('gives an empty caption and an empty footer no room', () => {
    mount();
    openFromButton();

    expect(getComputedStyle(el.querySelector('.caption')!).display).toBe('none');
    expect(getComputedStyle(el.querySelector('footer')!).display).toBe('none');
  });

  it('shows a caption and actions where they are given', () => {
    mount((h) => {
      h.caption.set(true);
      h.actions.set(true);
    });
    openFromButton();

    expect(el.querySelector('.caption #caption')).not.toBeNull();
    expect(el.querySelectorAll('footer button').length).toBe(2);
  });
});
