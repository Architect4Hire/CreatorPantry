import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, inject, input } from '@angular/core';

/**
 * The label, hint and error of one field, around a control the consumer projects.
 *
 * The control is projected rather than rendered here, so no template binding can reach it — which is why the
 * hint, the error and the required state are wired onto it from {@link constructor} after each render instead.
 * Without that the `*` and the hint are decoration: visible, and invisible to everyone who cannot see them.
 */
@Component({
  selector: 'cp-field',
  standalone: true,
  templateUrl: './field.component.html',
  // The shared drawing first, so this component's own sizing reads after it.
  styleUrls: ['./native-control.css', './field.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpFieldComponent {
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
