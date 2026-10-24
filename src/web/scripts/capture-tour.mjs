/**
 * Takes the landing page's product-tour screenshots (LP.5) from the running product, and writes the generated
 * `landing-tour.captures.ts` that decides which panels the tour shows.
 *
 *   CAPTURE_BASE_URL=https://localhost:7443 CAPTURE_EMAIL=... CAPTURE_PASSWORD=... npm run capture:tour
 *
 * It photographs a signed-in browser against a developer's own `aspire run`; nothing here mocks a screen the
 * product does not have, and nothing is drawn by hand. Two rules keep a real creator's work out of the landing
 * page: it refuses any workspace but the sample one named in SAMPLE_WORKSPACES, and it refuses to photograph a
 * screen that is empty — a panel whose work has not been done in that workspace fails the run by name instead of
 * shipping a picture of an empty state.
 *
 * What it cannot do is produce the AI work itself. Concepts, pictures, posts, outlines and voice guides cost
 * credits and a configured provider, so whoever runs this does that part once by hand in the sample workspace,
 * leaves the result on screen, and then runs the capture.
 */
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = join(here, '..');
const landing = join(web, 'src/app/features/landing');
const imagesDir = join(web, 'public/images/tour');

/** The only workspaces this may photograph. Adding one is a committed line, which is the point. */
const SAMPLE_WORKSPACES = ['maple-thyme'];

/** The capture's own window. The 1x file is half of this, which is the size the tour's frame draws. */
const VIEWPORT = { width: 1280, height: 800 };

/**
 * One panel per tour stop, in the component's order. `path` is where the screen lives; a deep link that needs an
 * id a script cannot guess — a draft, a guide, a piece of work — is supplied for that run instead, as
 * CAPTURE_URL_<PANEL> with the dashes turned to underscores, e.g. CAPTURE_URL_LONG_FORM.
 */
const PANELS = [
  {
    id: 'ai-recipe-studio',
    path: (workspace) => `/${workspace}/ai-recipe-studio`,
    heading: 'AI Recipe Studio',
    proof: /choose this concept/i,
    needs: 'a set of concepts on screen: brief the studio once and leave its answer showing.',
  },
  {
    id: 'image-studio',
    path: (workspace) => `/${workspace}/image-studio`,
    heading: 'Image Studio',
    // The studio's steps are tabs, and a run's pictures are the last of them.
    prepare: (page) => page.getByRole('tab', { name: /the pictures/i }).click(),
    proof: /in your library/i,
    needs: 'a finished run of pictures, with at least one saved to the library.',
  },
  {
    id: 'content-pipeline',
    requiresUrl: true,
    path: (workspace) => `/${workspace}/workflows/content-pipeline`,
    heading: 'Content Pipeline',
    proof: /instagram/i,
    needs: 'a pipeline run far enough to show the posts; pass its address as CAPTURE_URL_CONTENT_PIPELINE.',
  },
  {
    id: 'long-form',
    requiresUrl: true,
    path: (workspace) => `/${workspace}/workflows/long-form`,
    heading: 'Long-form',
    proof: /outline/i,
    needs: 'a draft stopped at its approved outline; pass its address as CAPTURE_URL_LONG_FORM.',
  },
  {
    id: 'brand-guide',
    requiresUrl: true,
    path: (workspace) => `/${workspace}/brand`,
    heading: 'Brand',
    proof: /voice guide/i,
    needs: 'an activated voice guide in the sample workspace.',
  },
  {
    id: 'publishing',
    requiresUrl: true,
    path: (workspace) => `/${workspace}/workflows`,
    heading: 'Workflows',
    proof: /scheduled|confirm/i,
    needs: 'a publish run at its confirm step; pass its address as CAPTURE_URL_PUBLISHING. Buffer has to be configured for this workspace (see .claude/rules/aspire.md), or the step has nothing to confirm.',
  },
];

const THEMES = /** @type {const} */ (['light', 'dark']);

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is not set. The capture signs in as a real account on a running app.`);
  return value;
}

/**
 * The component is the one list of panels; this script holds only where each one lives. Reading its ids back
 * means a panel added to the tour and forgotten here (or the other way round) fails the run rather than quietly
 * shipping five panels where the page promises six.
 */
async function assertPanelsMatchComponent() {
  const source = await readFile(join(landing, 'landing-tour.component.ts'), 'utf8');
  const stops = source.slice(source.indexOf('export const TOUR_STOPS'), source.indexOf('export const TOUR_HAS_CAPTURES'));
  const ids = [...stops.matchAll(/^\s*panel: '([a-z-]+)',$/gm)].map((match) => match[1]);
  if (ids.length === 0) throw new Error('Found no panels in landing-tour.component.ts. Has TOUR_STOPS moved?');
  const mine = PANELS.map((panel) => panel.id);
  if (ids.join() !== mine.join()) {
    throw new Error(`The tour's panels and this script's have drifted.\n  component: ${ids.join(', ')}\n  script:    ${mine.join(', ')}`);
  }
  return ids;
}

async function signIn(page, baseUrl) {
  await page.goto(`${baseUrl}/sign-in`, { waitUntil: 'networkidle' });
  await page.getByLabel(/email/i).fill(required('CAPTURE_EMAIL'));
  await page.getByLabel(/password/i).fill(required('CAPTURE_PASSWORD'));
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForURL((url) => !url.pathname.startsWith('/sign-in'), { timeout: 30_000 });
}

/**
 * A screen is worth photographing only if it is the right screen, the thing the panel promises is on it, and that
 * thing is what the picture will show. Each panel names the words that prove it — a concept to choose, a picture
 * in the library — and the proof has to be *visible*: present in the DOM is not enough, since a hidden tab panel
 * or a stretch far below the fold would otherwise pass while the photograph showed an empty form. The proof is
 * then scrolled into view, so the shot is of the work rather than of the brief above it.
 *
 * This is checked rather than the absence of a `cp-empty-state`, because a screen full of real work may still
 * carry a small empty note beside it ("nothing left to tidy away").
 */
async function assertShowsSomething(page, panel) {
  const heading = (await page.locator('h1').first().textContent())?.trim() ?? '';
  if (!heading.toLowerCase().includes(panel.heading.toLowerCase())) {
    throw new Error(`${panel.id}: expected a page headed "${panel.heading}", found "${heading}".`);
  }
  const proof = page.getByText(panel.proof).first();
  if ((await proof.count()) === 0 || !(await proof.isVisible())) {
    throw new Error(`${panel.id}: nothing on the screen shows ${panel.proof}. It needs ${panel.needs}`);
  }
  await proof.scrollIntoViewIfNeeded();
  await page.waitForTimeout(400);
}

/** Writes a shot at both densities in both formats, and answers with the 1x size the tour reserves for it. */
async function write(sharp, shot, panelId, theme) {
  const full = sharp(shot);
  const sizes = { '2x': VIEWPORT.width, '1x': VIEWPORT.width / 2 };
  for (const [density, width] of Object.entries(sizes)) {
    const resized = full.clone().resize({ width, fit: 'inside' });
    await resized.clone().avif({ quality: 55 }).toFile(join(imagesDir, `${panelId}-${theme}@${density}.avif`));
    await resized.clone().webp({ quality: 78 }).toFile(join(imagesDir, `${panelId}-${theme}@${density}.webp`));
  }
  return { width: VIEWPORT.width / 2, height: VIEWPORT.height / 2 };
}

/** A panel joins the tour only when all four of its files are on disk, in both themes. */
async function onDisk(id) {
  const files = new Set(await readdir(imagesDir).catch(() => []));
  return THEMES.every((theme) => ['1x', '2x'].every((density) => ['avif', 'webp'].every((format) => files.has(`${id}-${theme}@${density}.${format}`))));
}

/** What the generated file says today, so a panel this run did not photograph keeps the day it was photographed. */
async function alreadyListed() {
  const source = await readFile(join(landing, 'landing-tour.captures.ts'), 'utf8');
  const listed = new Map();
  for (const entry of source.matchAll(/\{ panel: '([a-z-]+)', width: (\d+), height: (\d+), capturedOn: '([\d-]+)' \}/g)) {
    listed.set(entry[1], { width: Number(entry[2]), height: Number(entry[3]), capturedOn: entry[4] });
  }
  return listed;
}

/**
 * The tour's panel list: what this run photographed in both themes, stamped today, plus what an earlier run
 * photographed and left on disk, keeping its own day. A panel that failed this time therefore keeps the pictures
 * and the date it already had rather than being restamped as if it had just been taken.
 */
async function panelList(ids, taken, today) {
  const listed = await alreadyListed();
  const captures = [];
  for (const id of ids) {
    const fresh = taken.get(id);
    if (fresh && fresh.themes.size === THEMES.length) captures.push([id, { ...fresh.size, capturedOn: today }]);
    else if (listed.has(id) && (await onDisk(id))) captures.push([id, listed.get(id)]);
  }
  return captures;
}

/** Rewrites the generated module the tour reads, so a capture needs no hand edit afterwards. */
async function writeCapturesModule(captures) {
  const path = join(landing, 'landing-tour.captures.ts');
  const source = await readFile(path, 'utf8');
  const entries = captures
    .map(([panel, entry]) => `  { panel: '${panel}', width: ${entry.width}, height: ${entry.height}, capturedOn: '${entry.capturedOn}' },`)
    .join('\n');
  const list = entries ? `[\n${entries}\n]` : '[]';
  const next = source.replace(
    /export const TOUR_CAPTURES: readonly LandingTourCapture\[\] = (\[[\s\S]*?\]);/,
    `export const TOUR_CAPTURES: readonly LandingTourCapture[] = ${list};`,
  );
  if (next === source) throw new Error('Could not find TOUR_CAPTURES in landing-tour.captures.ts to rewrite.');
  await writeFile(path, next);
}

async function main() {
  const baseUrl = required('CAPTURE_BASE_URL').replace(/\/$/, '');
  const workspace = process.env.CAPTURE_WORKSPACE ?? SAMPLE_WORKSPACES[0];
  if (!SAMPLE_WORKSPACES.includes(workspace)) {
    throw new Error(
      `The tour is photographed in a sample workspace only (${SAMPLE_WORKSPACES.join(', ')}), never a real creator's. ` +
        `"${workspace}" is not one; add it to SAMPLE_WORKSPACES if it is genuinely sample data.`,
    );
  }

  const ids = await assertPanelsMatchComponent();
  const { chromium } = await import('playwright');
  const { default: sharp } = await import('sharp');
  await mkdir(imagesDir, { recursive: true });

  const browser = await chromium.launch();
  /** panel id -> the themes photographed in this run, and the size they came out. */
  const taken = new Map();
  const failures = [];
  try {
    for (const theme of THEMES) {
      const context = await browser.newContext({
        viewport: VIEWPORT,
        deviceScaleFactor: 2,
        colorScheme: theme,
        reducedMotion: 'reduce',
        ignoreHTTPSErrors: true,
      });
      // The product remembers the theme rather than following the window, so the capture says which it wants.
      await context.addInitScript((value) => window.localStorage.setItem('creator-pantry-theme', value), theme);
      const page = await context.newPage();
      await signIn(page, baseUrl);

      for (const panel of PANELS) {
        const override = process.env[`CAPTURE_URL_${panel.id.replaceAll('-', '_').toUpperCase()}`];
        if (!override && panel.requiresUrl) {
          failures.push(`${panel.id} (${theme}): no address given. The screen this panel promises lives at an id of its own, and ${panel.path(workspace)} is only the way in. It needs ${panel.needs}`);
          continue;
        }
        const url = override ?? `${baseUrl}${panel.path(workspace)}`;
        try {
          await page.goto(url, { waitUntil: 'networkidle' });
          await page.evaluate(() => document.fonts.ready.then(() => undefined));
          if (panel.prepare) await panel.prepare(page);
          await page.waitForTimeout(1500);
          await assertShowsSomething(page, panel);
          const size = await write(sharp, await page.screenshot(), panel.id, theme);
          const already = taken.get(panel.id) ?? { size, themes: new Set() };
          already.themes.add(theme);
          taken.set(panel.id, already);
          console.log(`captured ${panel.id} (${theme}) from ${url}`);
        } catch (error) {
          failures.push(`${panel.id} (${theme}): ${error.message}`);
        }
      }
      await context.close();
    }
  } finally {
    await browser.close();
  }

  const captures = await panelList(ids, taken, new Date().toISOString().slice(0, 10));
  await writeCapturesModule(captures);
  const shown = captures.map(([id]) => id);
  console.log(`\nThe tour now shows ${shown.length} of ${ids.length} panels: ${shown.join(', ') || 'none'}.`);
  if (failures.length) {
    console.error(`\nNot captured:\n  ${failures.join('\n  ')}`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
