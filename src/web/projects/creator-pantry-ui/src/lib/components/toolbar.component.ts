import { ChangeDetectionStrategy, Component, ElementRef, inject, input } from '@angular/core';

@Component({
  selector: 'cp-toolbar', standalone: true,
  template: `<div class="cp-toolbar" role="toolbar" aria-orientation="horizontal" [attr.aria-label]="ariaLabel()" [attr.aria-disabled]="disabled() ? true : null" [class.cp-toolbar--disabled]="disabled()" (keydown)="onKeydown($event)">@if(loading()){<span class="status" role="status" aria-live="polite">Loading…</span>}<div class="group"><ng-content select="[cpToolbarSearch]" /></div><div class="group"><ng-content select="[cpToolbarFilters]" /></div><div class="group"><ng-content select="[cpToolbarActions]" /></div><details class="overflow"><summary><span class="chevron" aria-hidden="true">⋯</span>More actions</summary><div class="overflow-content"><ng-content select="[cpToolbarOverflow]" /></div></details></div>`,
  styles: [`
    :host { display:block; }
    .cp-toolbar { display:flex; flex-wrap:wrap; gap:var(--cp-space-3); align-items:center; }
    .cp-toolbar--disabled { opacity:.6; pointer-events:none; }
    .group { display:flex; flex-wrap:wrap; gap:var(--cp-space-2); align-items:center; }
    .status { font:400 var(--cp-font-size-sm)/var(--cp-leading-tight) var(--cp-font-sans); color:var(--cp-text-muted); }
    .overflow { position:relative; }
    /* A "More actions" control that opens onto nothing is worse than no control, so the disclosure is
       hidden until the consumer projects something into the overflow slot. The slot can only be measured
       once it exists, and it only exists because the disclosure is rendered — so this is a presence test
       on rendered children rather than an @if that would leave nothing to measure. display:none also
       drops the summary out of the roving-tabindex sweep below, which skips unrendered elements. */
    .overflow:not(:has(.overflow-content > *)) { display:none; }
    .overflow summary { list-style:none; display:inline-flex; align-items:center; gap:var(--cp-space-1); min-height:2.5rem; padding:0 var(--cp-space-3); border:1px solid var(--cp-border); border-radius:var(--cp-radius-md); background:var(--cp-surface); color:var(--cp-text); font:600 var(--cp-font-size-sm)/var(--cp-leading-tight) var(--cp-font-sans); cursor:pointer; user-select:none; }
    .overflow summary::-webkit-details-marker { display:none; }
    .overflow summary:hover { background:var(--cp-surface-subtle); border-color:var(--cp-border-strong); }
    .overflow[open] summary { border-color:var(--cp-primary); }
    .chevron { font-size:var(--cp-font-size-md); line-height:1; color:var(--cp-text-muted); }
    .overflow-content { display:none; position:absolute; top:calc(100% + var(--cp-space-1)); right:0; flex-direction:column; gap:var(--cp-space-1); padding:var(--cp-space-2); border:1px solid var(--cp-border); border-radius:var(--cp-radius-md); background:var(--cp-surface-raised); box-shadow:var(--cp-shadow-lg); min-width:10rem; z-index:var(--cp-z-overlay); }
    .overflow[open] .overflow-content { display:flex; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpToolbarComponent {
  readonly ariaLabel = input.required<string>();
  readonly loading = input(false);
  readonly disabled = input(false);

  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);

  onKeydown(event: KeyboardEvent): void {
    const key = event.key;
    if (key !== 'ArrowRight' && key !== 'ArrowDown' && key !== 'ArrowLeft' && key !== 'ArrowUp' && key !== 'Home' && key !== 'End') return;

    // Leave these keys alone when focus is in a control that owns them. A toolbar navigates its controls with
    // arrows, but inside a text field the same keys move the caret, and inside a select they change the value —
    // so stealing them would make the search slot this component advertises impossible to type in, and a select
    // in the filters slot impossible to operate without a mouse.
    if (this.ownsArrowKeys(event.target)) return;

    const focusables = Array.from(
      this.elementRef.nativeElement.querySelectorAll<HTMLElement>(
        'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"]), summary'
      )
      // Exclude descendants hidden inside a closed <details> (or otherwise not rendered): focusing them is a no-op in
      // real browsers, which would silently break wrap-around navigation.
    ).filter(el => el.offsetParent !== null);
    if (focusables.length === 0) return;

    const active = document.activeElement as HTMLElement | null;
    const currentIndex = active ? focusables.indexOf(active) : -1;
    let targetIndex: number;
    switch (key) {
      case 'ArrowRight':
      case 'ArrowDown':
        targetIndex = currentIndex === -1 ? 0 : (currentIndex + 1) % focusables.length;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        targetIndex = currentIndex === -1 ? 0 : (currentIndex - 1 + focusables.length) % focusables.length;
        break;
      case 'Home':
        targetIndex = 0;
        break;
      default:
        targetIndex = focusables.length - 1;
    }

    event.preventDefault();
    focusables[targetIndex].focus();
  }

  /**
   * Whether the focused element interprets arrow/Home/End keys itself: any `<select>` or `<textarea>`, and the
   * text-entry `<input>` types. Checkbox, radio and button inputs are excluded deliberately — they do not move a
   * caret, so roving navigation is right for them.
   */
  private ownsArrowKeys(target: EventTarget | null): boolean {
    if (!(target instanceof HTMLElement)) return false;

    const tag = target.tagName;
    if (tag === 'SELECT' || tag === 'TEXTAREA') return true;
    if (target.isContentEditable) return true;
    if (tag !== 'INPUT') return false;

    const type = (target as HTMLInputElement).type;
    return type !== 'checkbox' && type !== 'radio' && type !== 'button' && type !== 'submit' && type !== 'reset';
  }
}
