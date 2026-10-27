/**
 * Draws the landing page's social card (LP.10) — the picture a link to CreatorPantry shows when it is pasted
 * into a message, a post or a chat — and writes it to `public/images/og-card.png`.
 *
 *   npm run make:og-card
 *
 * Nothing on it is invented. The words are the hero's own headline, read out of the component so the card
 * cannot drift from the page it stands for; the logo is the file the page itself loads; the colours and the
 * typefaces are the design system's, taken from `tokens.css` and `themes.css` rather than typed in again. It
 * photographs none of the product and none of anyone's work: a card is seen by people who have not chosen to
 * see anything yet, which is the wrong audience for a screenshot of a real workspace.
 *
 * It is a committed file rather than a build step because a social scraper fetches it from the deployed site
 * long before any of this is installed, and because a card that changed silently under a link already shared
 * would be worse than one that changes when somebody means it to.
 */
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = join(here, '..');
const styles = join(web, 'projects/creator-pantry-ui/src/lib/styles');
const out = join(web, 'public/images/og-card.png');

/** The card's size. 1.91:1, which is what Open Graph, Twitter, Slack and LinkedIn all crop to. */
const CARD = { width: 1200, height: 630 };

/** The logo the page loads, inlined so the card does not depend on a server being up to draw it. */
const LOGO = join(web, 'public/images/logo-400.webp');

/** Where the words come from. Read, never retyped: a card that outlives a copy change is a card that lies. */
const HERO = join(web, 'src/app/features/landing/landing-hero.component.ts');

async function headline() {
  const source = await readFile(HERO, 'utf8');
  const match = source.match(/readonly headline = input\('([^']+)'\)/);
  if (!match) {
    throw new Error(`No default headline in ${HERO}. Has LandingHeroComponent's headline input moved?`);
  }
  return match[1];
}

/** The real token set, inlined, so the card is the product's paper and ink rather than a guess at them. */
async function tokens() {
  const [base, themes] = await Promise.all([
    readFile(join(styles, 'tokens.css'), 'utf8'),
    readFile(join(styles, 'themes.css'), 'utf8'),
  ]);
  return `${base}\n${themes}`;
}

async function card() {
  const logo = await readFile(LOGO);
  return `<!doctype html>
<html lang="en" data-cp-theme="light">
  <head>
    <meta charset="utf-8" />
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
    <link
      rel="stylesheet"
      href="https://fonts.googleapis.com/css2?family=DM+Sans:opsz,wght@9..40,400..700&family=Fredoka:wght@500;600;700&display=block"
    />
    <style>
      ${await tokens()}
      html, body { margin: 0; }
      body {
        width: ${CARD.width}px;
        height: ${CARD.height}px;
        display: flex;
        align-items: center;
        gap: 64px;
        padding: 0 80px;
        box-sizing: border-box;
        background: var(--cp-bg);
        color: var(--cp-text);
      }
      img { width: 380px; height: auto; flex: none; }
      p {
        margin: 0;
        font-family: var(--cp-font-display);
        font-size: 68px;
        font-weight: 600;
        line-height: 1.1;
        letter-spacing: var(--cp-tracking-display);
        text-wrap: balance;
      }
      /* The hero's own accent, on the hero's own last two words. */
      b { color: var(--cp-primary); font-weight: 600; }
    </style>
  </head>
  <body>
    <img src="data:image/webp;base64,${logo.toString('base64')}" alt="" />
    <p>${await heading()}</p>
  </body>
</html>`;
}

/** The headline, with its closing phrase in the action colour exactly as the hero underlines it. */
async function heading() {
  const text = await headline();
  const marked = 'everything else.';
  return text.endsWith(marked) ? `${text.slice(0, -marked.length)}<b>${marked}</b>` : text;
}

async function main() {
  const { chromium } = await import('playwright');
  const html = await card();

  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({ viewport: CARD, deviceScaleFactor: 1, colorScheme: 'light' });
    const page = await context.newPage();
    await page.setContent(html, { waitUntil: 'networkidle' });
    // `display: block` on the font face means the heading is never photographed in a fallback typeface.
    await page.evaluate(() => document.fonts.ready.then(() => undefined));
    await page.screenshot({ path: out, type: 'png' });
    await context.close();
  } finally {
    await browser.close();
  }

  console.log(`wrote ${out} (${CARD.width}x${CARD.height}) — "${await headline()}"`);
}

main().catch((error) => {
  console.error(error.message);
  process.exitCode = 1;
});
