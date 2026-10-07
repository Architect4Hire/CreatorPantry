import { BrandSourceAddFailure } from '../services/brand-source-document.service';

/**
 * Why one upload did not happen, in words a creator can act on.
 *
 * **Shared because it was already written twice** — `BrandLibraryUploadComponent` and
 * `BrandSourceReplaceComponent` each hold their own copy, and a third would be the point at which they start
 * disagreeing about what "corrupt" means. Those two are left as they are; new code uses this, and they could
 * adopt it without changing what either does.
 *
 * Every case is worded, including the two an upload cannot reach, because the vocabulary is shared with the
 * replacement path and an unworded case arrives at a creator as a blank.
 */
export const BRAND_SOURCE_ADD_FAILURE_MESSAGE: Readonly<Record<BrandSourceAddFailure, string>> = {
  unsupported:
    'That file is not one of the kinds the library can read. Try a PDF, Word document, Markdown, plain text, HTML, or an image.',
  too_large: 'That file is too big. Documents and images can be up to 20 MB, and text, Markdown and HTML up to 5 MB.',
  corrupt: 'That file looks damaged: it starts like the kind it claims to be, but could not be read through.',
  rejected: 'That file was turned away by the safety scan, so nothing was saved.',
  invalid: 'Something about this file could not be accepted. Check the name and try again.',
  forbidden: 'You need Editor access in this workspace to add a file.',
  not_found: 'This workspace could not be found. It may have been renamed.',
  key_reused: 'This upload was started with details that have since changed. Add it again.',
  cancelled: 'That upload was cancelled, so nothing was saved.',
  conflict: 'This file changed while you were adding it. Open it again to see where it stands.',
  archived_conflict: 'This file is on the shelf. Bring it back before changing it.',
  unavailable: 'That file could not be saved just now, and nothing was stored. Try again.',
};
