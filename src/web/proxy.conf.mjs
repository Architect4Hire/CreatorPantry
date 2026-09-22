// Development only. The SPA fetches /runtime-config.json from its own origin in every environment;
// here the dev server forwards it to CreatorPantry.Web, whose address Aspire injects.
const webHost = process.env['services__web__http__0'] ?? process.env['services__web__https__0'];
if (!webHost) {
  throw new Error('The web host address is not set. Start the development server through Aspire: `aspire run`.');
}

export default {
  '/runtime-config.json': { target: webHost, secure: false, changeOrigin: true },
};
