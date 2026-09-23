// Starts the Angular development server for Aspire (`aspire run`), which assigns PORT and injects the
// web host address used by proxy.conf.mjs. Running the dev server outside Aspire is unsupported.
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const port = process.env['PORT'];
if (!port) {
  console.error('PORT is not set. Start the development server through Aspire: `aspire run`.');
  process.exit(1);
}

// The gateway is HTTPS-only with SameSite=Strict cookies (antiforgery and session). Serving this dev
// server over plain HTTP would put the browser on a different scheme than the gateway — under Chrome's
// schemeful same-site policy that counts as cross-site, so the browser silently drops both cookies and
// every unsafe request fails CSRF validation. Matching the gateway's scheme keeps the two origins
// same-site (same scheme + host; the port difference doesn't matter for that check).
const certDir = fileURLToPath(new URL('../.dev-certs/', import.meta.url));
const certPath = `${certDir}localhost.pem`;
const keyPath = `${certDir}localhost.key`;

// Exported fresh per machine from the already-trusted ASP.NET Core dev cert (`dotnet dev-certs https
// --trust`, a normal prerequisite for running this solution at all) — never committed, never shared.
if (!existsSync(certPath) || !existsSync(keyPath)) {
  mkdirSync(certDir, { recursive: true });
  console.log('Exporting the local HTTPS dev certificate for the Angular dev server...');
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', certPath, '--format', 'Pem', '--no-password'], {
    stdio: 'inherit',
  });
}

// --host '::': ng serve's default host, "localhost", resolves the server to a single address family
// (observed as IPv6-only on macOS), so a browser whose "localhost" resolves to the other family first
// can never connect — the page hangs with no error. '::' binds dual-stack (both IPv4 and IPv6) so the
// server is reachable regardless of which family the browser tries.
const ng = fileURLToPath(new URL('../node_modules/@angular/cli/bin/ng.js', import.meta.url));
const child = spawn(
  process.execPath,
  [
    ng, 'serve', '--port', port, '--host', '::', '--proxy-config', 'proxy.conf.mjs',
    '--ssl', '--ssl-cert', certPath, '--ssl-key', keyPath,
  ],
  { stdio: 'inherit' },
);

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => child.kill(signal));
}
child.on('exit', (code) => process.exit(code ?? 1));
