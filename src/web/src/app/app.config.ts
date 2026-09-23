import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer } from '@angular/core';
import { provideRouter } from '@angular/router';
import { CpThemeService } from '@creator-pantry/ui';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth.interceptor';
import { RuntimeConfigService } from './core/runtime-config.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideHttpClient(withFetch(), withInterceptors([authInterceptor])),
    provideRouter(routes),
    provideAppInitializer(() => inject(RuntimeConfigService).load()),
    // Injecting the theme service here (rather than only from the shell) runs its constructor
    // before the router renders any route, so the persisted/system theme applies to every page —
    // including sign-in and sign-up, which sit outside the authenticated shell.
    provideAppInitializer(() => { inject(CpThemeService); }),
  ],
};
