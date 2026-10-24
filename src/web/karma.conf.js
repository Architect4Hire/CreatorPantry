// Karma configuration file, see link for more information
// https://karma-runner.github.io/1.0/config/configuration-file.html
//
// Shared by both test projects (creator-pantry-ui and showcase). It is Angular's generated default plus one
// change: specs run in headless Chrome at a fixed window size.
//
// Headless, because a visible Chrome only matches `:focus` while its window has system focus. Behind another
// window, an element focused by script is still `document.activeElement` but draws no focus ring, so every spec
// that checks a ring (checkbox, media card, page head, topic card) failed whenever the developer clicked anywhere
// else during a run. A headless page always has focus.
//
// A fixed size, because a visible window opens at whatever size the screen gives it, and headless opens at
// 800x600. Layout specs that measure where things land assume a desktop width; pinning the size makes them
// measure the same page on every machine and in CI.
//
// For a visible browser to debug in, pass `--browsers=Chrome` to `ng test`.

module.exports = function (config) {
  config.set({
    basePath: '',
    frameworks: ['jasmine'],
    plugins: [
      require('karma-jasmine'),
      require('karma-chrome-launcher'),
      require('karma-jasmine-html-reporter'),
      require('karma-coverage'),
    ],
    client: {
      jasmine: {
        // you can add configuration options for Jasmine here
        // the possible options are listed at https://jasmine.github.io/api/edge/Configuration.html
        // for example, you can disable the random execution with `random: false`
        // or set a specific seed with `seed: 4321`
      },
    },
    jasmineHtmlReporter: {
      suppressAll: true // removes the duplicated traces
    },
    coverageReporter: {
      dir: require('path').join(__dirname, './coverage'),
      subdir: '.',
      reporters: [
        { type: 'html' },
        { type: 'text-summary' }
      ]
    },
    reporters: ['progress', 'kjhtml'],
    customLaunchers: {
      ChromeHeadlessDesktop: {
        base: 'ChromeHeadless',
        flags: ['--window-size=1440,900'],
      },
    },
    browsers: ['ChromeHeadlessDesktop'],
    restartOnFileChange: true
  });
};
