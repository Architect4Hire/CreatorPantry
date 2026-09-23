// Starts the Angular development server for Aspire (`aspire run`), which assigns PORT and injects the
// web host address used by proxy.conf.mjs. Running the dev server outside Aspire is unsupported.
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const port = process.env['PORT'];
if (!port) {
  console.error('PORT is not set. Start the development server through Aspire: `aspire run`.');
  process.exit(1);
}

// --host '::': ng serve's default host, "localhost", resolves the server to a single address family
// (observed as IPv6-only on macOS), so a browser whose "localhost" resolves to the other family first
// can never connect — the page hangs with no error. '::' binds dual-stack (both IPv4 and IPv6) so the
// server is reachable regardless of which family the browser tries.
const ng = fileURLToPath(new URL('../node_modules/@angular/cli/bin/ng.js', import.meta.url));
const child = spawn(process.execPath, [ng, 'serve', '--port', port, '--host', '::', '--proxy-config', 'proxy.conf.mjs'], {
  stdio: 'inherit',
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => child.kill(signal));
}
child.on('exit', (code) => process.exit(code ?? 1));
