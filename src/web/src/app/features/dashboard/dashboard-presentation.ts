import { PublishingStatusValue } from '../../models/publishing.models';
import { TestIssueSeverity } from '../../models/recipe-test-run.models';

/** The tones `cp-status-pill` gives, each with a glyph of its own as well as a fill. */
export type DashboardTone = 'neutral' | 'progress' | 'success' | 'warning' | 'error';

export interface DashboardStatusWords {
  readonly label: string;
  readonly tone: DashboardTone;
}

/**
 * A publishing state in a word, with the same words and tones the card page's publishing controls use
 * (`publishingRecordView`), so a post reads the same on the dashboard as on its card.
 */
export const PUBLISHING_STATUS_WORDS: Readonly<Record<PublishingStatusValue, DashboardStatusWords>> = {
  Pending: { label: 'Waiting to send', tone: 'progress' },
  Submitting: { label: 'Sending', tone: 'progress' },
  Scheduled: { label: 'Scheduled', tone: 'success' },
  Published: { label: 'Published', tone: 'success' },
  Cancelling: { label: 'Cancelling', tone: 'progress' },
  Cancelled: { label: 'Cancelled', tone: 'neutral' },
  Failed: { label: 'Not published', tone: 'error' },
  NeedsAttention: { label: 'Needs attention', tone: 'warning' },
  Unrecognised: { label: 'State not recognised', tone: 'neutral' },
};

/** A test issue's severity in a word, with a tone that is never the only signal: the word is always shown. */
export const SEVERITY_WORDS: Readonly<Record<TestIssueSeverity, DashboardStatusWords>> = {
  Blocking: { label: 'Blocking', tone: 'error' },
  Major: { label: 'Major', tone: 'warning' },
  Minor: { label: 'Minor', tone: 'neutral' },
};

/** A network's own name, from the server's platform value. An unknown one is shown as it came. */
const PLATFORM_NAMES: Readonly<Record<string, string>> = {
  Instagram: 'Instagram',
  Facebook: 'Facebook',
  X: 'X',
  LinkedIn: 'LinkedIn',
  Pinterest: 'Pinterest',
  TikTok: 'TikTok',
  YouTube: 'YouTube',
  Mastodon: 'Mastodon',
  Bluesky: 'Bluesky',
  Threads: 'Threads',
  WhatsApp: 'WhatsApp',
  Other: 'Another network',
};

export function platformName(platform: string): string {
  return PLATFORM_NAMES[platform] ?? platform;
}
