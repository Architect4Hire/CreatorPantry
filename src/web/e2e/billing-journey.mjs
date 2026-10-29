// AP.6.5's Playwright journey, against a stubbed gateway and a stubbed payment provider.
//
// A creator on a trial buys a credit pack and then a plan: each button sends them to the provider's page, the
// provider sends them back, and the billing page waits — "Finishing your purchase…" — until the server says the
// webhook has landed, then says what was bought. Stripe test mode is not configured on the machine this was written
// on, so the gateway and the provider are stand-ins here; the real test-mode run is the one AP.6.5's checkpoint names
// (docs/prompts/1.creatorpantry-ai-pricing-scrub-prompts.md), once a Stripe test account is set up.
//
// Run after `npm run build`:  node e2e/billing-journey.mjs   (or `npm run e2e:billing`)
// Serves dist/showcase/browser itself, so nothing else has to be running.

import assert from 'node:assert/strict';
import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const ROOT = fileURLToPath(new URL('../dist/showcase/browser/', import.meta.url));
const PORT = 4317;
const APP = `http://localhost:${PORT}`;
const GATEWAY = 'https://gateway.test';
const PROVIDER = 'https://checkout.stripe.test';

// ---- the app, served as the web host serves it: files, with every other path the SPA's index ----------------

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.woff2': 'font/woff2', '.ico': 'image/x-icon', '.webp': 'image/webp' };

const server = createServer((request, response) => {
  const path = normalize(decodeURIComponent(new URL(request.url, APP).pathname)).replace(/^([\\/])+/, '');
  const file = join(ROOT, path);
  const target = file.startsWith(ROOT) && existsSync(file) && statSync(file).isFile() ? file : join(ROOT, 'index.html');
  response.writeHead(200, { 'content-type': TYPES[extname(target)] ?? 'application/octet-stream' });
  createReadStream(target).pipe(response);
});

// ---- what the stubbed server knows ------------------------------------------------------------------------

const plan = (key, name, monthlyPrice, credits, trialDays = null) => ({
  key, name, monthlyPrice, annualMonthlyPrice: monthlyPrice - 3, annualPrice: (monthlyPrice - 3) * 12, annualSaving: 36,
  credits, exampleMix: '', dailyImageCap: 25, qualityClasses: ['Draft', 'Standard'], recommended: key === 'creator',
  trialDays, trialImageCap: trialDays === null ? null : 10, picturesAtDefaultQuality: null, exampleCounts: null,
});

const catalogue = {
  currency: 'USD',
  plans: [plan('trial', 'Trial', 0, 1000, 14), plan('creator', 'Creator', 12, 4500), plan('pro', 'Pro', 49, 18000)],
  packs: [{ key: 'small', credits: 2500, price: 10 }],
  rates: { textDraft: 5, longFormSection: 10 },
  pictureClasses: [{ qualityClass: 'Draft', perPicture: 25 }, { qualityClass: 'Standard', perPicture: 50 }],
  defaultPictureClass: 'Standard',
};

const trial = { key: 'trial', name: 'Trial', credits: 1000, period: 'Once', dailyImageCap: 5, isTrial: true, trialEndsAt: '2026-11-12T09:00:00+00:00', trialEnded: false };
const creator = { key: 'creator', name: 'Creator', credits: 4500, period: 'Monthly', dailyImageCap: 25, isTrial: false, trialEndsAt: null, trialEnded: false };

/** The server's state, which only "the webhook" below moves — never a redirect. */
const state = { packBalance: 0, subscription: null, plan: trial, packAsks: 0, planAsks: 0, keys: new Set() };

/** How many times the page has to ask before the webhook "lands": the pending state is part of the journey. */
const PENDING_ANSWERS = 2;
const asked = { cs_test_pack: 0, cs_test_plan: 0 };

function billing() {
  return {
    paymentsAvailable: true, isTestMode: true, plan: state.plan, subscription: state.subscription,
    packBalance: state.packBalance, canSubscribe: state.subscription === null, canBuyPacks: true,
    upgradePlanKeys: state.subscription ? ['pro'] : [],
  };
}

const usage = {
  period: { unit: 'Credits', allowance: 1000, carriedOver: 0, consumed: 400, reserved: 0, remaining: 600, periodLength: 'Monthly', timeZoneId: 'Etc/UTC', startsAt: '2026-10-01T00:00:00+00:00', resetsAt: '2026-11-01T00:00:00+00:00' },
  isSuspended: false, isNearlySpent: false, pictures: 0, writing: 400, packRemaining: 0,
  lowCredit: { isNoticed: false, isDismissed: false, noticeEnabled: true, spendable: 600 },
  plan: null, byTask: [], byWorkspace: [],
};

/** Answers one gateway request the way the API would, or with a 404 for anything the page does not need. */
function answer(method, url, headers) {
  const path = url.pathname;

  if (path === '/bff/session') return [200, { authenticated: true, displayName: 'Sam' }];
  if (path === '/bff/antiforgery') return [200, { requestToken: 'token-1' }];
  if (path === '/api/v1/me' && method === 'GET') return [200, []];
  if (path === '/api/v1/me/ai-usage') return [200, usage];
  if (path === '/api/v1/plans') return [200, catalogue];
  if (path === '/api/v1/me/billing' && method === 'GET') return [200, billing()];

  if (method === 'POST' && path.startsWith('/api/v1/me/billing/')) {
    // Every command arrives with the antiforgery token and its own key, as the gateway requires.
    assert.equal(headers['x-xsrf-token'], 'token-1', 'a command carries the antiforgery token');
    assert.ok(headers['idempotency-key'], 'a purchase carries an Idempotency-Key');
    state.keys.add(headers['idempotency-key']);
  }

  if (method === 'POST' && path === '/api/v1/me/billing/checkout/pack') {
    state.packAsks++;
    return [200, { url: `${PROVIDER}/pay/cs_test_pack?kind=pack`, scheduled: false }];
  }

  if (method === 'POST' && path === '/api/v1/me/billing/checkout/subscription') {
    state.planAsks++;
    return [200, { url: `${PROVIDER}/pay/cs_test_plan?kind=plan`, scheduled: false }];
  }

  const session = path.match(/^\/api\/v1\/me\/billing\/checkout\/(cs_test_(?:pack|plan))$/);
  if (session) {
    const id = session[1];
    if (++asked[id] <= PENDING_ANSWERS) return [200, { state: 'Pending', planName: null, credits: null }];

    // The webhook has landed: the purchase is recorded, and the next read of the account shows it.
    if (id === 'cs_test_pack') {
      state.packBalance = 2500;
      return [200, { state: 'Complete', planName: null, credits: 2500 }];
    }

    state.plan = creator;
    state.subscription = { status: 'Active', planKey: 'creator', cycle: 'Monthly', currentPeriodEnd: '2026-12-01T00:00:00+00:00', cancelAt: null, nextPlanKey: null, nextPlanName: null, nextCycle: null, nextChangeAt: null };
    return [200, { state: 'Complete', planName: 'Creator', credits: null }];
  }

  return [404, { code: 'not_found', title: 'Not found.' }];
}

// ---- the journey -------------------------------------------------------------------------------------------

await new Promise((resolve) => server.listen(PORT, resolve));
const browser = await chromium.launch();

try {
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));

  await context.route('**/runtime-config.json', (route) => route.fulfill({ json: { gatewayUrl: GATEWAY } }));

  await context.route(`${GATEWAY}/**`, async (route) => {
    const request = route.request();
    const cors = {
      'access-control-allow-origin': APP,
      'access-control-allow-credentials': 'true',
      'access-control-allow-headers': 'content-type, x-xsrf-token, idempotency-key',
      'access-control-allow-methods': 'GET, POST, PUT, DELETE, OPTIONS',
    };

    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204, headers: cors });

    const [status, body] = answer(request.method(), new URL(request.url()), request.headers());
    return route.fulfill({ status, headers: { ...cors, 'content-type': 'application/json' }, body: JSON.stringify(body) });
  });

  // The provider's hosted page: one button that pays and sends the creator back, with the session, as Stripe does.
  await context.route(`${PROVIDER}/**`, (route) => {
    const url = new URL(route.request().url());
    const session = url.pathname.split('/').at(-1);
    const kind = url.searchParams.get('kind');
    return route.fulfill({
      contentType: 'text/html',
      body: `<!doctype html><title>Stripe test checkout</title><h1>Pay</h1>
        <a href="${APP}/app/billing?checkout=${kind}&session_id=${session}">Pay now</a>`,
    });
  });

  // 1. The billing page, on a trial.
  await page.goto(`${APP}/app/billing`);
  await page.getByRole('heading', { name: 'Billing', level: 1 }).waitFor();
  await page.getByText('Payments are in test mode').waitFor();
  await page.getByText('Trial ends').waitFor();
  await page.getByText('Prices before tax. Any sales tax, VAT or GST is added at checkout.').first().waitFor();

  // 2. Buy a pack: to the provider, pay, and back.
  await page.getByRole('button', { name: 'Buy 2,500 credits' }).click();
  await page.waitForURL(`${PROVIDER}/**`);
  await page.getByRole('link', { name: 'Pay now' }).click();
  await page.waitForURL(`${APP}/app/billing**`);

  // Pending until the webhook lands, then said, and the address is cleaned of the provider's query.
  await page.getByText('Finishing your purchase…').waitFor();
  await page.getByText('2,500 credits added.').waitFor({ timeout: 15_000 });
  await page.getByText('Pack credits').waitFor();
  assert.equal(new URL(page.url()).search, '', 'the return query is taken off the address');

  // 3. Choose a plan the same way.
  await page.getByRole('button', { name: 'Choose Creator' }).click();
  await page.waitForURL(`${PROVIDER}/**`);
  await page.getByRole('link', { name: 'Pay now' }).click();
  await page.waitForURL(`${APP}/app/billing**`);
  await page.getByText('Creator is active.').waitFor({ timeout: 15_000 });

  // The page now reads as an account that pays: the portal is offered, and the plan it is on is the chosen card.
  await page.getByRole('button', { name: 'Manage payment and invoices' }).waitFor();
  await page.getByRole('button', { name: 'Upgrade to Pro' }).waitFor();

  // 4. Coming back from a closed Checkout says nothing was charged.
  await page.goto(`${APP}/app/billing?checkout=cancelled`);
  await page.getByText('Checkout was closed. Nothing was charged.').waitFor();

  assert.equal(state.packAsks, 1, 'one pack Checkout was asked for');
  assert.equal(state.planAsks, 1, 'one plan Checkout was asked for');
  assert.equal(state.keys.size, 2, 'each purchase carried its own key');
  assert.deepEqual(errors, [], 'the page raised no errors');

  console.log('billing journey: passed — a pack and a plan bought through a stubbed provider, each waited for.');
} finally {
  await browser.close();
  server.close();
}
