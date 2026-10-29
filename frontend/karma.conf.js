module.exports = function(config) {
  config.set({
    frameworks: ['jasmine', '@angular-devkit/build-angular'],
    plugins: [require('karma-jasmine'), require('karma-chrome-launcher'), require('karma-jasmine-html-reporter'), require('karma-coverage'), require('@angular-devkit/build-angular/plugins/karma')],
    reporters: ['progress'],
    browsers: ['ChromiumHeadlessCI'],
    customLaunchers: { ChromiumHeadlessCI: { base: 'ChromeHeadless', flags: ['--no-sandbox', '--disable-dev-shm-usage'] } },
    singleRun: true,
    client: { clearContext: false }
  });
};
