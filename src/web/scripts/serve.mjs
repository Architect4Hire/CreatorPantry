// Starts the Angular development server for Aspire (`aspire run`), which assigns PORT and injects the
// web host address used by proxy.conf.mjs. Running the dev server outside Aspire is unsupported.
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const port = process.env['PORT'];
if (!port) {
  console.error('PORT is not set. Start the development server through Aspire: `aspire run`.');
  process.exit(1);
}

const ng = fileURLToPath(new URL('../node_modules/@angular/cli/bin/ng.js', import.meta.url));
const child = spawn(process.execPath, [ng, 'serve', '--port', port, '--proxy-config', 'proxy.conf.mjs'], {
  stdio: 'inherit',
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => child.kill(signal));
}
child.on('exit', (code) => process.exit(code ?? 1));
