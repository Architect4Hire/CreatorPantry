import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpDialogComponent } from './dialog.component';
import { CpDrawerCloseReason, CpDrawerComponent, CpDrawerSize } from './drawer.component';

@Component({
  imports: [CpDrawerComponent, CpDialogComponent],
  template: `<button id="opener" type="button">Edit details</button>
    <button id="elsewhere" type="button">Elsewhere</button>
    <cp-drawer
      [open]="open()"
      heading="Edit details"
      [description]="description()"
      [size]="size()"
      (closeRequested)="requests.push($event)"
    >
      @if (fields()) {
        <input id="first" [attr.autofocus]="autofocus() ? '' : null" />
        <input id="handles-escape" (keydown.escape)="$event.preventDefault()" />
      }
      @if (actions()) {
        <button cpDrawerActions id="save" type="button">Save</button>
      }
    </cp-drawer>
    <cp-dialog [open]="dialogOpen()" title="A dialog"><button id="in-dialog" type="button">Inside</button></cp-dialog>`,
})
class HostComponent {
  readonly open = signal(false);
  readonly description = signal('');
  readonly size = signal<CpDrawerSize>('standard');
  readonly fields = signal(true);
  readonly actions = signal(true);
  readonly autofocus = signal(false);
  readonly dialogOpen = signal(false);
  readonly requests: CpDrawerCloseReason[] = [];
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

function openFrom(id = 'opener'): void {
  byId(id).focus();
  host.open.set(true);
  fixture.detectChanges();
}

function close(): void {
  host.open.set(false);
  fixture.detectChanges();
}

// The drawer moves itself to <body>, so it is found from the document, never from the fixture.
const panel = (): HTMLElement | null => document.body.querySelector<HTMLElement>(':scope > section.panel');
const scrim = (): HTMLElement | null => document.body.querySelector<HTMLElement>(':scope > div.scrim');
const byId = (id: string): HTMLElement => document.getElementById(id)!;

function press(target: HTMLElement, key: string, shiftKey = false): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  return event;
}

describe('CpDrawerComponent', () => {
  beforeEach(() => {
    document.documentElement.style.setProperty('--cp-drawer-width', '400px');
    document.documentElement.style.setProperty('--cp-drawer-width-wide', '700px');
    document.documentElement.style.setProperty('--cp-z-overlay', '90');
    document.documentElement.style.setProperty('--cp-z-modal', '100');
  });

  afterEach(() => {
    fixture?.destroy();
    el?.remove();
    for (const name of ['--cp-drawer-width', '--cp-drawer-width-wide', '--cp-z-overlay', '--cp-z-modal']) {
      document.documentElement.style.removeProperty(name);
    }
  });

  it('renders nothing while closed', () => {
    mount();
    expect(panel()).toBeNull();
    expect(scrim()).toBeNull();
  });

  it('is a modal dialog named by its heading and described by its description', () => {
    mount((h) => h.description.set('Shown beside the picture.'));
    openFrom();

    const drawer = panel()!;
    expect(drawer.getAttribute('role')).toBe('dialog');
    expect(drawer.getAttribute('aria-modal')).toBe('true');
    const heading = drawer.querySelector('h2')!;
    expect(heading.textContent).toBe('Edit details');
    expect(drawer.getAttribute('aria-labelledby')).toBe(heading.id);
    const description = document.getElementById(drawer.getAttribute('aria-describedby')!)!;
    expect(description.textContent).toBe('Shown beside the picture.');
  });

  it('is not described by anything when there is no description', () => {
    mount();
    openFrom();
    expect(panel()!.hasAttribute('aria-describedby')).toBeFalse();
    expect(panel()!.querySelector('header p')).toBeNull();
  });

  it('moves focus to the panel when it opens, and back to what opened it when it closes', () => {
    mount();
    openFrom();
    expect(document.activeElement).toBe(panel());

    close();
    expect(panel()).toBeNull();
    expect(document.activeElement).toBe(byId('opener'));
  });

  it('returns focus to the opener when a confirmation raised over it holds focus as it closes', () => {
    mount();
    openFrom();
    const discard = document.createElement('button');
    document.body.appendChild(discard);
    discard.focus();

    close();
    expect(document.activeElement).toBe(byId('opener'));
    discard.remove();
  });

  it('gives focus to an autofocus element in the body instead', () => {
    mount((h) => h.autofocus.set(true));
    openFrom();
    expect(document.activeElement).toBe(byId('first'));
  });

  it('keeps Tab inside, wrapping at both ends', () => {
    mount();
    openFrom();
    const closeButton = panel()!.querySelector<HTMLElement>('button.close')!;
    const save = byId('save');

    // From the panel itself, where focus starts, Shift+Tab goes to the last stop.
    expect(press(panel()!, 'Tab', true).defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(save);

    expect(press(save, 'Tab').defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(closeButton);

    expect(press(closeButton, 'Tab', true).defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(save);

    // In the middle, Tab is the browser's.
    byId('first').focus();
    expect(press(byId('first'), 'Tab').defaultPrevented).toBeFalse();
  });

  it('makes the rest of the page inert, and puts it back exactly', () => {
    mount();
    const alreadyInert = document.createElement('div');
    alreadyInert.setAttribute('inert', '');
    document.body.appendChild(alreadyInert);

    openFrom();
    expect(el.hasAttribute('inert')).toBeTrue();
    expect(panel()!.hasAttribute('inert')).toBeFalse();
    expect(scrim()!.hasAttribute('inert')).toBeFalse();

    // Something raised over the drawer afterwards — a confirmation — is not touched.
    const confirmation = document.createElement('div');
    document.body.appendChild(confirmation);
    expect(confirmation.hasAttribute('inert')).toBeFalse();

    close();
    expect(el.hasAttribute('inert')).toBeFalse();
    expect(alreadyInert.hasAttribute('inert')).withContext('was inert before, stays inert').toBeTrue();

    alreadyInert.remove();
    confirmation.remove();
  });

  it('locks body scroll while open and restores the inline styles it found', () => {
    mount();
    document.body.style.overflow = 'clip';
    document.body.style.paddingRight = '7px';

    openFrom();
    expect(document.body.style.overflow).toBe('hidden');

    close();
    expect(document.body.style.overflow).toBe('clip');
    expect(document.body.style.paddingRight).toBe('7px');

    document.body.style.overflow = '';
    document.body.style.paddingRight = '';
    openFrom();
    close();
    expect(document.body.getAttribute('style') ?? '').not.toContain('overflow');
    expect(document.body.getAttribute('style') ?? '').not.toContain('padding');
  });

  it('asks to close on Escape, the scrim and the close control, and stays open', () => {
    mount();
    openFrom();

    expect(press(byId('first'), 'Escape').defaultPrevented).toBeTrue();
    scrim()!.click();
    panel()!.querySelector<HTMLElement>('button.close')!.click();
    fixture.detectChanges();

    expect(host.requests).toEqual(['escape', 'scrim', 'close-button']);
    expect(panel()).not.toBeNull();
  });

  it('leaves Escape to a control inside that has already handled it', () => {
    mount();
    openFrom();
    press(byId('handles-escape'), 'Escape');
    expect(host.requests).toEqual([]);
  });

  it('still asks to close on Escape when focus has been lost to the body', () => {
    mount();
    openFrom();
    // What a task inside does when it removes or disables the control that held focus.
    (document.activeElement as HTMLElement).blur();
    expect(document.activeElement).toBe(document.body);

    press(document.body, 'Escape');

    expect(host.requests).toEqual(['escape']);
    // And focus is back inside, so the next key has somewhere to land.
    expect(document.activeElement).toBe(panel());
  });

  it('brings Tab back into the drawer when focus has been lost to the body', () => {
    mount();
    openFrom();
    (document.activeElement as HTMLElement).blur();

    const event = press(document.body, 'Tab');

    expect(event.defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(panel());
    expect(host.requests).toEqual([]);
  });

  it('leaves stray keys alone while a confirmation is raised over it', () => {
    mount();
    openFrom();
    const confirmation = document.createElement('div');
    document.body.appendChild(confirmation);
    (document.activeElement as HTMLElement).blur();

    const tab = press(document.body, 'Tab');
    press(document.body, 'Escape');

    // The confirmation's keys are its own, even with nothing in it focused.
    expect(tab.defaultPrevented).toBeFalse();
    expect(host.requests).toEqual([]);
    expect(document.activeElement).toBe(document.body);
    confirmation.remove();
  });

  it('stops listening for stray keys once it has closed', () => {
    mount();
    openFrom();
    close();
    (document.activeElement as HTMLElement | null)?.blur();

    press(document.body, 'Escape');

    expect(host.requests).toEqual([]);
  });

  it('names the close control', () => {
    mount();
    openFrom();
    const closeButton = panel()!.querySelector<HTMLElement>('button.close')!;
    expect(closeButton.getAttribute('aria-label')).toBe('Close');
    expect(closeButton.getBoundingClientRect().width).toBeGreaterThanOrEqual(40);
    expect(closeButton.getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
  });

  it('takes its two widths from the tokens', () => {
    if (window.innerWidth <= 800) pending('the window is narrower than the widths under test');
    mount();
    openFrom();
    expect(panel()!.getBoundingClientRect().width).toBe(400);

    host.size.set('wide');
    fixture.detectChanges();
    expect(panel()!.getBoundingClientRect().width).toBe(700);
  });

  it('sits against the right edge, full height, the scrim under it on the overlay layer', () => {
    mount();
    openFrom();
    // Read from the computed offsets rather than the box, which is mid-slide just after opening.
    const style = getComputedStyle(panel()!);
    expect(style.position).toBe('fixed');
    expect(style.right).toBe('0px');
    expect(style.top).toBe('0px');
    expect(style.bottom).toBe('0px');
    expect(getComputedStyle(scrim()!).zIndex).toBe('90');
    expect(getComputedStyle(panel()!).zIndex).toBe('100');
  });

  it('scrolls its body, not itself, and gives an empty footer no room', () => {
    mount((h) => h.actions.set(false));
    openFrom();
    expect(getComputedStyle(panel()!.querySelector('.body')!).overflowY).toBe('auto');
    expect(getComputedStyle(panel()!.querySelector('footer')!).display).toBe('none');

    host.actions.set(true);
    fixture.detectChanges();
    expect(getComputedStyle(panel()!.querySelector('footer')!).display).toBe('flex');
  });

  it('keeps Tab on the close control when it is the only stop', () => {
    mount((h) => {
      h.fields.set(false);
      h.actions.set(false);
    });
    openFrom();
    const closeButton = panel()!.querySelector<HTMLElement>('button.close')!;
    closeButton.focus();
    expect(press(closeButton, 'Tab').defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(closeButton);
  });

  it('cleans up after itself when destroyed while open', () => {
    mount();
    openFrom();
    fixture.destroy();

    expect(panel()).toBeNull();
    expect(scrim()).toBeNull();
    expect(document.body.style.overflow).toBe('');
    expect(document.body.querySelector(':scope > [inert]')).toBeNull();
  });
});

describe('CpDialogComponent, sharing the modal surface', () => {
  afterEach(() => {
    fixture?.destroy();
    el?.remove();
  });

  function openDialog(): HTMLElement {
    byId('opener').focus();
    host.dialogOpen.set(true);
    fixture.detectChanges();
    return el.querySelector<HTMLElement>('cp-dialog section')!;
  }

  it('stays in place, locks scroll, and returns focus to what opened it', () => {
    mount();
    const dialog = openDialog();
    expect(document.activeElement).toBe(dialog);
    expect(document.body.style.overflow).toBe('hidden');
    expect(el.hasAttribute('inert')).toBeFalse();

    host.dialogOpen.set(false);
    fixture.detectChanges();
    expect(document.body.style.overflow).toBe('');
    expect(document.activeElement).toBe(byId('opener'));
  });

  it('leaves focus where the consumer put it while closing', () => {
    mount();
    openDialog();
    // A consumer that closes and deliberately focuses something else has already answered the question.
    host.dialogOpen.set(false);
    byId('elsewhere').focus();
    fixture.detectChanges();
    expect(document.activeElement).toBe(byId('elsewhere'));
  });

  it('keeps Tab inside', () => {
    mount();
    const dialog = openDialog();
    const closeButton = dialog.querySelector<HTMLElement>('header button')!;
    const inside = byId('in-dialog');

    inside.focus();
    expect(press(inside, 'Tab').defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(closeButton);
    expect(press(closeButton, 'Tab', true).defaultPrevented).toBeTrue();
    expect(document.activeElement).toBe(inside);
  });
});
