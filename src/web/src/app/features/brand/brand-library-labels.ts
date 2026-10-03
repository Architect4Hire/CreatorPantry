import { BrandSourceDocumentType, BrandSourcePurpose } from '../../models/brand-source-document.models';
import { BrandSourceExtractionState } from '../../models/brand-source-extraction.models';

/**
 * How the Brand Library writes the vocabularies the API sends as enum names.
 *
 * Exhaustive by type on purpose: a value added to one of these enums has to be given words here, instead of
 * reaching the screen as the raw name a creator never chose and would not recognise.
 */

export const DOCUMENT_TYPE_LABELS: Record<BrandSourceDocumentType, string> = {
  StyleGuide: 'Style guide',
  WritingSample: 'Writing sample',
  PublishedPost: 'Published post',
  Newsletter: 'Newsletter',
  SocialSample: 'Social post',
  VisualReference: 'Visual reference',
  Other: 'Other',
};

export const PURPOSE_LABELS: Record<BrandSourcePurpose, string> = {
  Voice: 'Voice',
  WritingStyle: 'Writing style',
  VisualDirection: 'Visual direction',
  Background: 'Background',
  NotMyVoice: "Doesn't sound like me",
};

/**
 * What each extraction state means for the creator, in terms of the thing they care about: whether this
 * example can be read and used as grounding yet.
 *
 * Deliberately not a claim about the document's quality or its contents — only about whether its text has
 * been read.
 */
export const EXTRACTION_STATE_LABELS: Record<BrandSourceExtractionState, string> = {
  NotExtracted: 'Not read yet',
  Succeeded: 'Text ready',
  Unsupported: 'No text to read',
  Failed: "Couldn't read text",
};
