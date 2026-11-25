import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors, withNoXsrfProtection } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { sessionEndedInterceptor, xsrfInterceptor } from './auth/interceptors';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // State-changing calls send X-XSRF-TOKEN from the XSRF-TOKEN cookie (contracts/panel-api.md).
    // xsrfInterceptor replaces the built-in one: it also fetches a missing or stale token.
    provideHttpClient(
      withFetch(),
      withNoXsrfProtection(),
      withInterceptors([sessionEndedInterceptor, xsrfInterceptor]),
    ),
  ],
};
