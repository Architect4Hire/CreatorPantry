import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, inject, input } from '@angular/core';
/**
 * The label, hint and error of one field, around a control the consumer projects.
 *
 * The control is projected rather than rendered here, so no template binding can reach it — which is why the
 * hint, the error and the required state are wired onto it from {@link constructor} after each render instead.
 * Without that the `*` and the hint are decoration: visible, and invisible to everyone who cannot see them.
 */
@Component({
 selector:'cp-field', standalone:true,
 template:`<label [for]="forId()">{{label()}} @if(required()){<span aria-hidden="true">*</span>}</label><ng-content />@if(hint()){<small [id]="hintId()">{{hint()}}</small>}@if(error()){<small class="error" [id]="errorId()" role="alert">{{error()}}</small>}@if(warning()){<small class="warning" [id]="warningId()"><span class="sr-only">Heads up: </span>{{warning()}}</small>}`,
 styles:[`
  /* Rows sized to their content: a field in a grid row beside a taller one keeps its control at its own height. */
  :host{display:grid;align-content:start;gap:var(--cp-space-2)}
  label{font-size:var(--cp-font-size-sm);font-weight:600;line-height:1.3;color:var(--cp-text)}
  label span{margin-inline-start:.125rem;color:var(--cp-danger)}
  small{font-size:var(--cp-font-size-sm);line-height:1.45;color:var(--cp-text-muted)}
  /* An error says so with a glyph as well as a colour, so it never depends on telling two hues apart. */
  .error{display:flex;align-items:baseline;gap:var(--cp-space-2);font-size:var(--cp-font-size-sm);font-weight:600;color:var(--cp-danger)}
  .error::before{content:'!';flex:none;display:inline-grid;place-items:center;width:1.05rem;height:1.05rem;border-radius:var(--cp-radius-full);background:var(--cp-danger);color:var(--cp-ink-on-accent);font-size:.6875rem;font-weight:700;line-height:1;transform:translateY(.1rem)}
  /*
   * The field itself. A white sheet with a border dark enough to find (--cp-border-control, 3:1), a hairline
   * of inset depth, and on focus a soft ring of the action colour inside the global outline. The outline is
   * made transparent rather than removed so a forced-colours user, for whom box-shadow is not painted, still
   * gets the system's own focus line.
   */
  :host ::ng-deep input,:host ::ng-deep textarea,:host ::ng-deep select{
    box-sizing:border-box;width:100%;min-height:2.75rem;padding:.65rem .85rem;
    border:1px solid var(--cp-border-control);border-radius:var(--cp-radius-md);
    background:var(--cp-field-bg);color:var(--cp-text);line-height:1.4;
    box-shadow:inset 0 1px 2px color-mix(in srgb,var(--cp-text) 6%,transparent);
    transition:border-color var(--cp-duration-fast) var(--cp-ease),box-shadow var(--cp-duration-fast) var(--cp-ease),background-color var(--cp-duration-fast)}
  :host ::ng-deep textarea{min-height:6rem;line-height:var(--cp-leading-normal);resize:vertical}
  :host ::ng-deep input::placeholder,:host ::ng-deep textarea::placeholder{color:var(--cp-text-faint);opacity:1}
  :host ::ng-deep input:hover,:host ::ng-deep textarea:hover,:host ::ng-deep select:hover{border-color:var(--cp-text-muted)}
  :host ::ng-deep input:focus-visible,:host ::ng-deep textarea:focus-visible,:host ::ng-deep select:focus-visible,
  :host ::ng-deep input:focus,:host ::ng-deep textarea:focus,:host ::ng-deep select:focus{
    border-color:var(--cp-primary);outline:2px solid transparent;outline-offset:0;
    box-shadow:0 0 0 var(--cp-ring-width) var(--cp-focus)}
  /* A warning is advice, not a refusal: a different glyph (a triangle against the error's disc) and the warning
     ink, so the two differ in shape and not only in hue. It never changes the control's own edge. */
  .warning{display:flex;align-items:baseline;gap:var(--cp-space-2);font-size:var(--cp-font-size-sm);font-weight:600;color:var(--cp-orange)}
  .warning::before{content:'\\25B2';flex:none;font-size:.7rem;line-height:1;transform:translateY(-.05rem)}
  .sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap}
  :host ::ng-deep [aria-invalid='true']{border-color:var(--cp-danger)}
  :host ::ng-deep [aria-invalid='true']:focus{box-shadow:0 0 0 var(--cp-ring-width) color-mix(in srgb,var(--cp-danger) 28%,transparent)}
  :host ::ng-deep input:disabled,:host ::ng-deep textarea:disabled,:host ::ng-deep select:disabled,
  :host ::ng-deep input[readonly],:host ::ng-deep textarea[readonly]{
    border-color:var(--cp-border-strong);background:var(--cp-surface-subtle);color:var(--cp-text-muted);box-shadow:none}
  :host ::ng-deep input:disabled,:host ::ng-deep textarea:disabled,:host ::ng-deep select:disabled{cursor:not-allowed}
  /* A drawn caret, so a select reads as a choice and not as a text box. Two gradient triangles in the muted
     ink rather than an image, so it follows the theme with no second asset. */
  :host ::ng-deep select:not([multiple]){
    appearance:none;padding-inline-end:2.5rem;cursor:pointer;
    background-image:linear-gradient(45deg,transparent 50%,var(--cp-text-muted) 50%),linear-gradient(135deg,var(--cp-text-muted) 50%,transparent 50%);
    background-position:calc(100% - 1.15rem) 52%,calc(100% - .8rem) 52%;
    background-size:.35rem .35rem;background-repeat:no-repeat}
 `],
 changeDetection:ChangeDetectionStrategy.OnPush
}) export class CpFieldComponent {
  readonly label=input.required<string>();
  readonly forId=input.required<string>();
  readonly hint=input('');
  readonly error=input('');
  /** Advice about the value that does not stop anything: it shows beside the control and never marks it invalid. */
  readonly warning=input('');
  readonly required=input(false);

  protected readonly hintId=computed(()=>`${this.forId()}-hint`);
  protected readonly errorId=computed(()=>`${this.forId()}-error`);
  protected readonly warningId=computed(()=>`${this.forId()}-warning`);

  private readonly elementRef:ElementRef<HTMLElement>=inject(ElementRef);

  /** Whether `aria-invalid` on the control is this field's to take away again — see {@link constructor}. */
  private markedInvalid=false;

  constructor() {
    afterRenderEffect(() => {
      // An attribute selector, not `#id`: these ids carry uuids and other spellings a CSS id selector would
      // have to escape. It finds the control wherever it sits, including one nested inside another component
      // — a combobox's own input takes this id, and is the thing a screen reader lands on.
      const control = this.elementRef.nativeElement.querySelector(`[id="${this.forId()}"]`);
      if (control === null) return;

      // Merged rather than assigned. A consumer may describe its own control by something this field knows
      // nothing about — a banner explaining why the control is disabled, say — and overwriting that would
      // trade one missing announcement for another. Only the two ids this field owns are replaced, which is
      // also what makes running again after every render idempotent.
      const owned = new Set([this.hintId(), this.errorId(), this.warningId()]);
      const theirs = (control.getAttribute('aria-describedby') ?? '')
        .split(/\s+/)
        .filter((id) => id.length > 0 && !owned.has(id));
      const describedBy = [
        ...theirs,
        ...(this.hint() ? [this.hintId()] : []),
        ...(this.error() ? [this.errorId()] : []),
        ...(this.warning() ? [this.warningId()] : []),
      ];

      if (describedBy.length === 0) control.removeAttribute('aria-describedby');
      else control.setAttribute('aria-describedby', describedBy.join(' '));

      // `aria-invalid` follows the error, so a template no longer binds it by hand on every control. Set when an
      // error shows and removed when it clears — but only if this field set it: a consumer that marks its own
      // control invalid for a reason this field does not know about keeps that mark.
      if (this.error()) {
        control.setAttribute('aria-invalid', 'true');
        this.markedInvalid = true;
      } else if (this.markedInvalid) {
        control.removeAttribute('aria-invalid');
        this.markedInvalid = false;
      }

      // `aria-required`, never the native `required`: this states a fact for assistive technology and leaves
      // browser validation UI — and the moment it fires — to the consumer, whose form owns that decision.
      if (this.required()) control.setAttribute('aria-required', 'true');
      else control.removeAttribute('aria-required');
    });
  }
}
