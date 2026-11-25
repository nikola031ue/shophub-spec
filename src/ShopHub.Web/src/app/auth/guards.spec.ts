import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { guestGuard, ownerGuard } from './guards';
import { answerSession, settle } from './testing';

describe('guards', () => {
  let http: HttpTestingController;
  let router: Router;
  const route = {} as ActivatedRouteSnapshot;
  const state = { url: '/shops/s7k2m4qxab' } as RouterStateSnapshot;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => http.verify());

  const run = (guard: typeof ownerGuard) =>
    TestBed.runInInjectionContext(() => guard(route, state)) as Promise<boolean | UrlTree>;

  it('ownerGuard lets a signed-in owner through', async () => {
    const result = run(ownerGuard);
    await settle();
    answerSession(http, 'owner@example.test');
    expect(await result).toBe(true);
  });

  it('ownerGuard sends everyone else to sign-in with a return URL', async () => {
    const result = run(ownerGuard);
    await settle();
    answerSession(http, null);
    expect(router.serializeUrl((await result) as UrlTree)).toBe('/signin?returnUrl=%2Fshops%2Fs7k2m4qxab');
  });

  it('ownerGuard treats an unreachable API as signed out', async () => {
    const result = run(ownerGuard);
    await settle();
    http.expectOne('/api/session').flush(null, { status: 503, statusText: 'Unavailable' });
    expect(router.serializeUrl((await result) as UrlTree)).toContain('/signin');
  });

  it('guestGuard sends a signed-in owner to the panel and lets guests in', async () => {
    const signedIn = run(guestGuard);
    await settle();
    answerSession(http, 'owner@example.test');
    expect(router.serializeUrl((await signedIn) as UrlTree)).toBe('/');

    const guest = run(guestGuard);
    await settle();
    answerSession(http, null);
    expect(await guest).toBe(true);
  });
});
