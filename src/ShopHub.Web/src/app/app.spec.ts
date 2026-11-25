import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { AuthApi } from './auth/auth-api';
import { answerSession, settle } from './auth/testing';

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows the ShopHub header and a router outlet, no account while signed out', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h1')?.textContent).toContain('ShopHub');
    expect(el.querySelector('router-outlet')).not.toBeNull();
    expect(el.textContent).not.toContain('Sign out');
  });

  it('shows the email and signs out (this session only) to the sign-in page', async () => {
    const auth = TestBed.inject(AuthApi);
    const refreshed = auth.refresh();
    answerSession(http, 'owner@example.test');
    await refreshed;

    const router = TestBed.inject(Router);
    vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('owner@example.test');

    el.querySelector<HTMLButtonElement>('.account button')!.click();
    await settle();
    http.expectOne({ method: 'POST', url: '/api/signout' }).flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    await fixture.whenStable();

    expect(auth.signedIn()).toBe(false);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/signin');
    expect(el.textContent).not.toContain('owner@example.test');
  });
});
