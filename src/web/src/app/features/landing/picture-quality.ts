import { GeneratedImageQualityClass } from '../../models/generated-image.models';

/**
 * What the public page calls each picture quality, short enough for a card line and a table cell (AP.4.5).
 *
 * **Short, where the credit menu's labels are sentences.** The menu prices "A standard picture"; a plan card
 * lists "Draft, Standard, High" and the comparison table has one cell for the best a plan reaches, so the same
 * words would not fit either place. Both read the catalogue's own class names, which the server publishes in
 * one spelling (`High2K`), so neither has to know how `Billing:Plans` spells them.
 */
const PICTURE_QUALITY_SHORT: Readonly<Record<GeneratedImageQualityClass, string>> = {
  Draft: 'Draft',
  Standard: 'Standard',
  High: 'High',
  // No comma inside a label: a card lists them comma-joined, and "High, 2K" would read as two qualities.
  // The studio's picker has room for the comma and uses it; this is the other reason the two differ.
  High2K: 'High 2K',
  High4K: 'High 4K',
};

/**
 * The page's word for a published quality class, or null for one this bundle does not know.
 *
 * Null rather than the raw key: a class added on the server is then simply not named here, which reads as a
 * shorter list, where printing `high-8k` would show a reader the shape of a configuration file.
 */
export function pictureQualityLabel(qualityClass: string): string | null {
  return PICTURE_QUALITY_SHORT[qualityClass as GeneratedImageQualityClass] ?? null;
}
