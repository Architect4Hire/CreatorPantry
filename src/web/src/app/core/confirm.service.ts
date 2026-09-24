import { Injectable } from '@angular/core';
import Swal from 'sweetalert2';

export interface ConfirmRequest {
  readonly title: string;
  readonly message: string;
  /** The label on the button that proceeds. Name the action, never "OK". */
  readonly confirmLabel: string;
  readonly cancelLabel: string;
  /**
   * `danger` for anything that destroys work the creator cannot get back — discarding edits, deleting an
   * asset. `neutral` for a decision that is merely worth pausing on.
   */
  readonly tone?: 'danger' | 'neutral';
}

/**
 * Modal confirmations, on SweetAlert2.
 *
 * **Why this exists as a service.** SweetAlert2 is imported here and nowhere else, so the whole application
 * asks one question — "did the creator agree?" — and no component holds a third-party dialog API. That keeps
 * the dependency swappable, and it makes every confirmation stubbable in a test without rendering a real
 * modal or driving its DOM.
 *
 * **Where this is used, and where `CpDialogComponent` still is.** This is for confirmations: a question with
 * two answers, one of which loses work. `CpDialogComponent` remains the right thing for a modal that *hosts*
 * something — the workspace-creation form, for instance — because that is a surface with its own content,
 * validation and focus order rather than a yes/no.
 *
 * **What it deliberately cannot do.** A browser close, refresh or full navigation cannot be confirmed by any
 * library: the browser shows its own message and ignores whatever the page would rather say. That path stays a
 * `beforeunload` listener, and the two are not interchangeable.
 */
@Injectable({ providedIn: 'root' })
export class ConfirmService {
  async confirm(request: ConfirmRequest): Promise<boolean> {
    const result = await Swal.fire({
      title: request.title,
      text: request.message,
      showCancelButton: true,
      confirmButtonText: request.confirmLabel,
      cancelButtonText: request.cancelLabel,

      // Cancel is focused, not confirm: the confirming button is the one that loses the creator's work, so a
      // reflexive Enter must not be what discards it.
      focusCancel: true,
      reverseButtons: true,

      // Styled entirely through the classes below, against --cp-* tokens, so a confirmation inherits the
      // product's theme — including dark mode — rather than arriving with this library's own palette.
      buttonsStyling: false,
      customClass: {
        popup: 'cp-swal',
        title: 'cp-swal__title',
        htmlContainer: 'cp-swal__body',
        actions: 'cp-swal__actions',
        confirmButton: request.tone === 'neutral' ? 'cp-swal__confirm' : 'cp-swal__confirm cp-swal__confirm--danger',
        cancelButton: 'cp-swal__cancel',
      },
    });

    // Anything that is not an explicit confirmation — Cancel, Escape, a click on the backdrop — is "stay".
    // Defaulting the other way would make dismissing a dialog destroy work.
    return result.isConfirmed;
  }
}
