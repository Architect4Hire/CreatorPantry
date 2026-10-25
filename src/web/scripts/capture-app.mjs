/**
 * Photographs every signed-in page of the app (AU.0, APP-001): in light and dark, at a phone's width and a
 * desktop's, once in the sample workspace with work in it and once in an empty workspace this run creates for
 * itself, plus each page's loading state. The pictures are the baseline the UX uplift is measured against, and
 * the same run with another label is the "after".
 *
 *   CAPTURE_BASE_URL=https://localhost:7443 CAPTURE_EMAIL=... CAPTURE_PASSWORD=... npm run capture:app
 *   CAPTURE_LABEL=after npm run capture:app
 *
 * It signs in and keeps to workspaces the way capture-tour.mjs does: a real account on a developer's own
 * `aspire run`, and never a workspace but the sample one and the one it made. Every address is checked against
 * that before the page is opened and again after it has settled, so a redirect cannot carry the camera into a
 * real creator's workspace. Nothing it writes is committed: `.captures/` is ignored.
 *
 * A detail page needs an id. The script reads it from the first link to such a page on the page that lists them
 * (the first recipe in Recipes, the first picture in Pictures), which in the empty workspace finds nothing — so
 * those pages are photographed in the sample workspace only. An id no list links to is supplied for the run as
 * CAPTURE_URL_<PAGE>, with the dashes turned to underscores, e.g. CAPTURE_URL_BRAND_GUIDE.
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = join(here, '..');

/**
 * capture-tour.mjs's list, for the same reason, plus `homespun-favorites`: the owner's sample workspace for the
 * app's own baseline (AU.0), which never feeds the landing page. A workspace added to the tour's is added here.
 */
const SAMPLE_WORKSPACES = ['maple-thyme', 'homespun-favorites'];

/**
 * The run's own workspace is named with this, and an earlier run's may be reused by slug (CAPTURE_EMPTY_WORKSPACE)
 * only while its name still starts with it. There is no way to delete a workspace, so reusing one is how a
 * machine avoids collecting a new empty workspace on every run.
 */
const EMPTY_WORKSPACE_PREFIX = 'Capture empty';

const VIEWPORTS = /** @type {const} */ ([
  { name: '375', width: 375, height: 812 },
  { name: '1440', width: 1440, height: 900 },
]);
const THEMES = /** @type {const} */ (['light', 'dark']);

/** How long the loading pass holds back a page's workspace data, and how far into that it takes the picture. */
const LOADING_DELAY_MS = 8_000;
const LOADING_SHOT_AFTER_MS = 1_200;

/**
 * Every signed-in routed page in app.routes.ts and the feature route files, in the nav's order. `path` is a
 * fixed address; `discover` finds one on a listing page; `from` builds one onto another page's address;
 * `requiresUrl` means only CAPTURE_URL_<PAGE> can say where it is. `account` pages are outside any workspace.
 *
 * Left out on purpose: the workspace gate at /app, which an account with a workspace only passes through on its
 * way into the first one (and so would photograph whichever workspace that is), and the `context/:id` addresses
 * of the studios and pipelines, which are the same pages as the bare ones holding a piece of work.
 */
const PAGES = [
  { id: 'dashboard', path: (ws) => `/${ws}/dashboard` },
  { id: 'workflows', path: (ws) => `/${ws}/workflows` },
  { id: 'workflow-run', discover: { on: (ws) => `/${ws}/workflows`, href: (ws) => new RegExp(`^/${ws}/workflows/runs/[^/?#]+$`) } },
  { id: 'content-pipeline', path: (ws) => `/${ws}/workflows/content-pipeline` },
  { id: 'long-form', path: (ws) => `/${ws}/workflows/long-form` },
  { id: 'quick-recipe', path: (ws) => `/${ws}/workflows/quick-recipe` },
  { id: 'my-day', path: (ws) => `/${ws}/my-day` },
  { id: 'my-week', path: (ws) => `/${ws}/my-week` },
  { id: 'recipes', path: (ws) => `/${ws}/recipes` },
  { id: 'recipe-new', path: (ws) => `/${ws}/recipes/new` },
  { id: 'recipe-editor', discover: { on: (ws) => `/${ws}/recipes`, href: (ws) => new RegExp(`^/${ws}/recipes/(?!new$)[^/?#]+$`) } },
  { id: 'recipe-kitchen', from: { page: 'recipe-editor', suffix: '/kitchen' } },
  { id: 'ai-recipe-studio', path: (ws) => `/${ws}/ai-recipe-studio` },
  { id: 'recipe-first-draft', path: (ws) => `/${ws}/ai-recipe-studio/draft` },
  { id: 'image-studio', path: (ws) => `/${ws}/image-studio` },
  { id: 'social-studio', path: (ws) => `/${ws}/social-studio` },
  { id: 'pictures', path: (ws) => `/${ws}/dam` },
  { id: 'picture', discover: { on: (ws) => `/${ws}/dam`, href: (ws) => new RegExp(`^/${ws}/dam/[^/?#]+$`) } },
  { id: 'content-board', path: (ws) => `/${ws}/content-board` },
  { id: 'content-card', discover: { on: (ws) => `/${ws}/content-board`, href: (ws) => new RegExp(`^/${ws}/content-board/[^/?#]+$`) } },
  { id: 'brand', path: (ws) => `/${ws}/brand` },
  { id: 'brand-setup', path: (ws) => `/${ws}/brand/setup` },
  { id: 'brand-library', path: (ws) => `/${ws}/brand/library` },
  { id: 'brand-example', discover: { on: (ws) => `/${ws}/brand/library`, href: (ws) => new RegExp(`^/${ws}/brand/library/[^/?#]+$`) } },
  { id: 'brand-guide', requiresUrl: true, needs: 'the address of a voice guide, /<workspace>/brand/style-guide/<guideId>.' },
  { id: 'brand-guide-history', from: { page: 'brand-guide', suffix: '/history' } },
  { id: 'brand-test-drive', from: { page: 'brand-guide', suffix: '/test-drive' } },
  { id: 'prompt-library', path: (ws) => `/${ws}/prompt-library` },
  { id: 'prompt', discover: { on: (ws) => `/${ws}/prompt-library`, href: (ws) => new RegExp(`^/${ws}/prompt-library/[^/?#]+$`) } },
  { id: 'settings', path: (ws) => `/${ws}/settings` },
  { id: 'themed-days', path: (ws) => `/${ws}/settings/themed-days` },
  { id: 'ai-usage', account: true, path: () => '/app/ai-usage' },
];

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is not set. The capture signs in as a real account on a running app.`);
  return value;
}

function overrideFor(pageId) {
  return process.env[`CAPTURE_URL_${pageId.replaceAll('-', '_').toUpperCase()}`];
}

async function signIn(page, baseUrl) {
  await page.goto(`${baseUrl}/sign-in`, { waitUntil: 'networkidle' });
  await page.getByLabel(/email/i).fill(required('CAPTURE_EMAIL'));
  await page.getByLabel(/password/i).fill(required('CAPTURE_PASSWORD'));
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForURL((url) => !url.pathname.startsWith('/sign-in'), { timeout: 30_000 });
}

/**
 * Calls the API from inside the signed-in page, the way the app does: the gateway's origin from
 * runtime-config.json, the session cookie, and the antiforgery token as X-XSRF-TOKEN on anything unsafe.
 */
async function api(page, method, path, body) {
  return page.evaluate(
    async ({ method, path, body }) => {
      const { gatewayUrl } = await (await fetch('/runtime-config.json')).json();
      const headers = { 'Content-Type': 'application/json' };
      if (method !== 'GET') {
        const antiforgery = await fetch(new URL('/bff/antiforgery', gatewayUrl), { credentials: 'include' });
        headers['X-XSRF-TOKEN'] = (await antiforgery.json()).requestToken;
      }
      const response = await fetch(new URL(path, gatewayUrl), {
        method,
        credentials: 'include',
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      const text = await response.text();
      return { status: response.status, body: text ? JSON.parse(text) : null };
    },
    { method, path, body },
  );
}

async function memberships(page) {
  const response = await api(page, 'GET', '/api/v1/me');
  if (response.status !== 200 || !Array.isArray(response.body)) throw new Error(`GET /api/v1/me answered ${response.status}.`);
  return response.body.filter((entry) => entry.status === 'Active');
}

/** The run's empty workspace: an earlier run's when CAPTURE_EMPTY_WORKSPACE names one, otherwise a new one. */
async function emptyWorkspace(page, label) {
  const reuse = process.env.CAPTURE_EMPTY_WORKSPACE;
  if (reuse) {
    const mine = (await memberships(page)).find((entry) => entry.workspaceSlug === reuse);
    if (!mine?.workspaceName.startsWith(EMPTY_WORKSPACE_PREFIX)) {
      throw new Error(`CAPTURE_EMPTY_WORKSPACE="${reuse}" is not a workspace an earlier capture made ("${EMPTY_WORKSPACE_PREFIX} …").`);
    }
    return reuse;
  }
  const stamp = new Date().toISOString().slice(0, 16).replace('T', ' ');
  const created = await api(page, 'POST', '/api/v1/workspaces', { name: `${EMPTY_WORKSPACE_PREFIX} ${label} ${stamp}` });
  if (created.status !== 201 || typeof created.body?.slug !== 'string') {
    throw new Error(`Could not create the run's empty workspace: POST /api/v1/workspaces answered ${created.status}.`);
  }
  console.log(`created the empty workspace "${created.body.slug}" (reuse it next time with CAPTURE_EMPTY_WORKSPACE=${created.body.slug})`);
  return created.body.slug;
}

/** The workspaces this run may photograph, and whether an account-level page may be photographed at all. */
function guardFor(workspace) {
  return (pathname) => {
    const first = pathname.split('/')[1] ?? '';
    if (first === 'app') return;
    if (first !== workspace) throw new Error(`refused: ${pathname} is outside the workspace being photographed ("${workspace}").`);
  };
}

/**
 * The AI allowance page answers for every workspace the account works in, so it is photographed only when every
 * one of those is the sample workspace or one a capture made.
 */
async function accountPagesAllowed(page) {
  const others = (await memberships(page)).filter(
    (entry) => !SAMPLE_WORKSPACES.includes(entry.workspaceSlug) && !entry.workspaceName.startsWith(EMPTY_WORKSPACE_PREFIX),
  );
  return others.length === 0 ? null : `the account also belongs to ${others.length} other workspace(s), which this page would show.`;
}

/**
 * Waits until the page has stopped arriving: no network, the fonts in, and nothing in <main> still marked busy.
 * A page that never stops saying it is busy is photographed anyway and noted, since that is a finding too.
 */
async function settle(page) {
  await page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => undefined);
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  const quiet = await page
    .waitForFunction(() => !document.querySelector('main [aria-busy="true"]'), null, { timeout: 10_000 })
    .then(() => true)
    .catch(() => false);
  await page.waitForTimeout(600);
  return quiet ? null : 'still marked aria-busy after 10 s';
}

async function heading(page) {
  return ((await page.locator('h1').first().textContent({ timeout: 2_000 }).catch(() => '')) ?? '').trim();
}

/**
 * Where each page lives in one workspace: a fixed path, a link found on the page that lists them, an address
 * built onto another page's, or — in the sample workspace only — one supplied for the run.
 */
async function resolveAddresses(page, baseUrl, workspace, isSample) {
  const guard = guardFor(workspace);
  const found = new Map();
  const missing = new Map();
  for (const entry of PAGES) {
    const override = isSample ? overrideFor(entry.id) : undefined;
    try {
      if (override) {
        const url = new URL(override, baseUrl);
        guard(url.pathname);
        found.set(entry.id, url.pathname + url.search);
      } else if (entry.path) {
        found.set(entry.id, entry.path(workspace));
      } else if (entry.from) {
        const base = found.get(entry.from.page);
        if (base) found.set(entry.id, base.split('?')[0] + entry.from.suffix);
        else missing.set(entry.id, `needs ${entry.from.page}'s address, which was not found.`);
      } else if (entry.discover) {
        await page.goto(`${baseUrl}${entry.discover.on(workspace)}`, { waitUntil: 'networkidle' });
        await settle(page);
        const pattern = entry.discover.href(workspace);
        const hrefs = await page.locator('main a[href]').evaluateAll((links) => links.map((link) => link.getAttribute('href') ?? ''));
        const href = hrefs.find((candidate) => pattern.test(candidate));
        if (href) found.set(entry.id, href);
        else missing.set(entry.id, `no link on ${entry.discover.on(workspace)} matches ${pattern}.`);
      } else if (entry.requiresUrl) {
        missing.set(entry.id, isSample ? `no address given. Pass ${entry.needs}` : 'not applicable in an empty workspace.');
      }
    } catch (error) {
      missing.set(entry.id, error.message);
    }
  }
  return { found, missing };
}

async function photographSet({ browser, storageState, baseUrl, outDir, set, workspace, addresses, accountRefusal }) {
  const guard = guardFor(workspace);
  const shots = [];
  const variants = THEMES.flatMap((theme) => VIEWPORTS.map((viewport) => ({ theme, viewport })));
  await Promise.all(
    variants.map(async ({ theme, viewport }) => {
      const context = await browser.newContext({
        storageState,
        viewport: { width: viewport.width, height: viewport.height },
        colorScheme: theme,
        reducedMotion: 'reduce',
        ignoreHTTPSErrors: true,
      });
      // The product remembers the theme rather than following the window, so the capture says which it wants.
      await context.addInitScript((value) => window.localStorage.setItem('creator-pantry-theme', value), theme);
      const page = await context.newPage();
      for (const entry of PAGES) {
        const shot = { page: entry.id, set, theme, width: viewport.width };
        const address = addresses.found.get(entry.id);
        if (entry.account && set !== 'sample') continue;
        if (!address || (entry.account && accountRefusal)) {
          shots.push({ ...shot, skipped: addresses.missing.get(entry.id) ?? accountRefusal });
          continue;
        }
        try {
          guard(new URL(address, baseUrl).pathname);
          await page.goto(`${baseUrl}${address}`, { waitUntil: 'domcontentloaded' });
          const note = await settle(page);
          guard(new URL(page.url()).pathname);
          const file = join(set, `${entry.id}-${theme}-${viewport.name}.png`);
          await page.screenshot({ path: join(outDir, file), fullPage: true });
          shots.push({ ...shot, url: new URL(page.url()).pathname, heading: await heading(page), file, note });
        } catch (error) {
          shots.push({ ...shot, failed: error.message });
        }
      }
      await context.close();
    }),
  );
  return shots;
}

/**
 * Each page caught while its data is still on the way: the workspace's own requests (and the allowance's) are
 * held back, so what is photographed is whatever the page shows before an answer — a sentence, a skeleton, or
 * nothing. Light theme at the desktop width only; the rubric asks what loading looks like, not how it reflows.
 */
async function photographLoading({ browser, storageState, baseUrl, outDir, workspace, addresses, accountRefusal }) {
  const guard = guardFor(workspace);
  const shots = [];
  const context = await browser.newContext({ storageState, viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce', ignoreHTTPSErrors: true });
  await context.addInitScript(() => window.localStorage.setItem('creator-pantry-theme', 'light'));
  const held = new RegExp(`/api/v1/(workspaces/${workspace}/|me/ai-usage)`);
  await context.route(held, async (route) => {
    await new Promise((resolve) => setTimeout(resolve, LOADING_DELAY_MS));
    await route.continue().catch(() => undefined);
  });
  const page = await context.newPage();
  for (const entry of PAGES) {
    const address = addresses.found.get(entry.id);
    const shot = { page: entry.id, set: 'loading', theme: 'light', width: 1440 };
    if (!address || (entry.account && accountRefusal)) continue;
    try {
      guard(new URL(address, baseUrl).pathname);
      await page.goto(`${baseUrl}${address}`, { waitUntil: 'domcontentloaded' });
      await page.locator('h1').first().waitFor({ timeout: 15_000 }).catch(() => undefined);
      await page.waitForTimeout(LOADING_SHOT_AFTER_MS);
      guard(new URL(page.url()).pathname);
      const file = join('loading', `${entry.id}.png`);
      await page.screenshot({ path: join(outDir, file) });
      const busy = await page.locator('main [aria-busy="true"], main [role="status"]').allTextContents();
      shots.push({ ...shot, url: new URL(page.url()).pathname, file, saying: busy.map((text) => text.trim()).filter(Boolean) });
    } catch (error) {
      shots.push({ ...shot, failed: error.message });
    }
  }
  await context.close().catch(() => undefined);
  return shots;
}

/** One sheet per set and width: the top of every page, light beside dark, each labelled with its page. */
async function contactSheet(sharp, outDir, shots, set, width) {
  const tile = width === 1440 ? { w: 480, h: 300 } : { w: 188, h: 406 };
  const rows = PAGES.map((entry) => entry.id)
    .map((id) => THEMES.map((theme) => shots.find((shot) => shot.page === id && shot.set === set && shot.theme === theme && shot.width === width && shot.file)))
    .filter((pair) => pair.some(Boolean));
  if (rows.length === 0) return null;
  const perRow = width === 1440 ? 2 : 4;
  const cell = { w: tile.w * 2 + 12, h: tile.h + 28 };
  const columns = perRow / 2;
  const sheet = { w: columns * cell.w + (columns + 1) * 16, h: Math.ceil(rows.length / columns) * (cell.h + 16) + 16 };
  const layers = [];
  for (const [index, pair] of rows.entries()) {
    const x = 16 + (index % columns) * (cell.w + 16);
    const y = 16 + Math.floor(index / columns) * (cell.h + 16);
    const name = (pair.find(Boolean)).page;
    layers.push({ input: Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${cell.w}" height="24"><text x="0" y="17" font-family="sans-serif" font-size="15" font-weight="700" fill="#222">${name}</text></svg>`), left: x, top: y });
    for (const [side, shot] of pair.entries()) {
      if (!shot) continue;
      const image = await sharp(join(outDir, shot.file)).resize({ width: tile.w, height: tile.h, fit: 'cover', position: 'top' }).toBuffer();
      layers.push({ input: image, left: x + side * (tile.w + 12), top: y + 26 });
    }
  }
  const file = `contact-${set}-${width}.png`;
  await sharp({ create: { width: sheet.w, height: sheet.h, channels: 3, background: '#e9e6df' } }).composite(layers).png().toFile(join(outDir, file));
  return file;
}

/** A page to look through the run with: every page as a row, every variant as a column, each linking to its file. */
async function contactPage(outDir, label, shots, workspaces) {
  const columns = [
    ...['sample', 'empty'].flatMap((set) => VIEWPORTS.flatMap((viewport) => THEMES.map((theme) => ({ set, theme, width: viewport.width })))),
    { set: 'loading', theme: 'light', width: 1440 },
  ];
  const cellFor = (id, column) => {
    const shot = shots.find((s) => s.page === id && s.set === column.set && s.theme === column.theme && s.width === column.width);
    if (!shot) return '<td class="none">—</td>';
    if (shot.file) return `<td><a href="${shot.file.replaceAll('\\', '/')}"><img loading="lazy" src="${shot.file.replaceAll('\\', '/')}" alt=""></a>${shot.note ? `<small>${shot.note}</small>` : ''}</td>`;
    return `<td class="none"><small>${escapeHtml(shot.skipped ?? shot.failed ?? '')}</small></td>`;
  };
  const html = `<!doctype html><meta charset="utf-8"><title>Captures: ${label}</title>
<style>body{font:14px/1.4 system-ui,sans-serif;margin:16px;background:#f4f1ea;color:#222}table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px;vertical-align:top}th{position:sticky;top:0;background:#fff;font-size:12px}td img{display:block;width:160px;max-height:220px;object-fit:cover;object-position:top}td.none{color:#777;max-width:160px}small{display:block;max-width:160px}</style>
<h1>Captures: ${label}</h1><p>Sample workspace <b>${workspaces.sample}</b>; empty workspace <b>${workspaces.empty}</b>.</p>
<table><tr><th>page</th>${columns.map((c) => `<th>${c.set}<br>${c.theme} ${c.width}</th>`).join('')}</tr>
${PAGES.map((entry) => `<tr><th>${entry.id}</th>${columns.map((c) => cellFor(entry.id, c)).join('')}</tr>`).join('\n')}
</table>`;
  await writeFile(join(outDir, 'index.html'), html);
}

function escapeHtml(text) {
  return text.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
}

async function main() {
  const baseUrl = required('CAPTURE_BASE_URL').replace(/\/$/, '');
  const label = process.env.CAPTURE_LABEL ?? 'before';
  if (!/^[a-z0-9][a-z0-9-]*$/.test(label)) throw new Error(`CAPTURE_LABEL "${label}" must be lower-case letters, digits and dashes.`);
  const sample = process.env.CAPTURE_WORKSPACE ?? SAMPLE_WORKSPACES[0];
  if (!SAMPLE_WORKSPACES.includes(sample)) {
    throw new Error(
      `The app is photographed in a sample workspace only (${SAMPLE_WORKSPACES.join(', ')}), never a real creator's. ` +
        `"${sample}" is not one; add it to SAMPLE_WORKSPACES here and in capture-tour.mjs if it is genuinely sample data.`,
    );
  }

  const outDir = join(web, '.captures', label);
  for (const folder of ['sample', 'empty', 'loading']) await mkdir(join(outDir, folder), { recursive: true });
  const { chromium } = await import('playwright');
  const { default: sharp } = await import('sharp');

  const browser = await chromium.launch();
  const shots = [];
  const workspaces = { sample, empty: '' };
  try {
    // One sign-in for the run; every theme and width reuses its session rather than signing in again.
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, ignoreHTTPSErrors: true, reducedMotion: 'reduce' });
    const page = await context.newPage();
    await signIn(page, baseUrl);
    if (!(await memberships(page)).some((entry) => entry.workspaceSlug === sample)) {
      throw new Error(`The account does not belong to the sample workspace "${sample}".`);
    }
    workspaces.empty = await emptyWorkspace(page, label);
    const accountRefusal = await accountPagesAllowed(page);
    const sampleAddresses = await resolveAddresses(page, baseUrl, sample, true);
    const emptyAddresses = await resolveAddresses(page, baseUrl, workspaces.empty, false);
    const storageState = await context.storageState();
    await context.close();

    const common = { browser, storageState, baseUrl, outDir, accountRefusal };
    shots.push(...(await photographSet({ ...common, set: 'sample', workspace: sample, addresses: sampleAddresses })));
    console.log('photographed the sample workspace');
    shots.push(...(await photographSet({ ...common, set: 'empty', workspace: workspaces.empty, addresses: emptyAddresses })));
    console.log('photographed the empty workspace');
    shots.push(...(await photographLoading({ ...common, workspace: sample, addresses: sampleAddresses })));
    console.log('photographed the loading states');
  } finally {
    await browser.close();
  }

  const sheets = [];
  for (const set of ['sample', 'empty']) {
    for (const viewport of VIEWPORTS) {
      const file = await contactSheet(sharp, outDir, shots, set, viewport.width);
      if (file) sheets.push(file);
    }
  }
  await contactPage(outDir, label, shots, workspaces);
  await writeFile(join(outDir, 'manifest.json'), JSON.stringify({ label, takenAt: new Date().toISOString(), baseUrl, workspaces, shots }, null, 2));

  const taken = shots.filter((shot) => shot.file).length;
  const failed = shots.filter((shot) => shot.failed);
  const skipped = shots.filter((shot) => shot.skipped);
  console.log(`\n${taken} pictures in ${outDir}; contact sheets ${sheets.join(', ')} and index.html.`);
  if (skipped.length) {
    const reasons = new Map(skipped.map((shot) => [`${shot.page} (${shot.set})`, shot.skipped]));
    console.log(`\nNot photographed:\n  ${[...reasons].map(([page, why]) => `${page}: ${why}`).join('\n  ')}`);
  }
  if (failed.length) {
    console.error(`\nFailed:\n  ${failed.map((shot) => `${shot.page} (${shot.set}, ${shot.theme} ${shot.width}): ${shot.failed}`).join('\n  ')}`);
    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
