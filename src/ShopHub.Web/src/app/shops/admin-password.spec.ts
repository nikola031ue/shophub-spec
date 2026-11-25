import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AdminPassword, FIRST_SIGNIN_HINT, NOT_READY, PASSWORD_CHANGED } from './admin-password';
import { shop, wait } from './testing';

describe('AdminPassword (FR-016)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AdminPassword],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function click() {
    const fixture = TestBed.createComponent(AdminPassword);
    fixture.componentRef.setInput('shop', shop());
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    // Nothing is fetched until the owner asks.
    http.expectNone('/api/shops/s7k2m4qxab/admin-password');
    el.querySelector('button')!.click();
    await wait(0);
    return { fixture, el };
  }

  it('on request shows the password, the first sign-in hint and the admin link', async () => {
    const { fixture, el } = await click();
    http.expectOne({ method: 'POST', url: '/api/shops/s7k2m4qxab/admin-password' }).flush({ password: 'gen-pass-123' });
    await wait(0);
    await fixture.whenStable();

    expect(el.querySelector('code')?.textContent).toBe('gen-pass-123');
    expect(el.textContent).toContain(FIRST_SIGNIN_HINT);
    expect(FIRST_SIGNIN_HINT).toContain('first sign-in');
    const link = el.querySelector<HTMLAnchorElement>('a')!;
    expect(link.getAttribute('href')).toBe('https://s7k2m4qxab.shops.localhost/admin/login');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('hiding drops the password from the page', async () => {
    const { fixture, el } = await click();
    http.expectOne('/api/shops/s7k2m4qxab/admin-password').flush({ password: 'gen-pass-123' });
    await wait(0);
    await fixture.whenStable();

    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes('Hide'))!.click();
    await fixture.whenStable();

    expect(el.textContent).not.toContain('gen-pass-123');
    expect(el.textContent).toContain('Show shop admin password');
  });

  it('says the password was changed, and shows none', async () => {
    const { fixture, el } = await click();
    http.expectOne('/api/shops/s7k2m4qxab/admin-password').flush(
      { title: 'changed', reason: 'changed' },
      { status: 409, statusText: 'Conflict' },
    );
    await wait(0);
    await fixture.whenStable();

    expect(el.textContent).toContain(PASSWORD_CHANGED);
    expect(el.querySelector('code')).toBeNull();
  });

  it('says when the shop is not ready', async () => {
    const { fixture, el } = await click();
    http.expectOne('/api/shops/s7k2m4qxab/admin-password').flush(
      { title: 'x', reason: 'notReady' },
      { status: 409, statusText: 'Conflict' },
    );
    await wait(0);
    await fixture.whenStable();

    expect(el.textContent).toContain(NOT_READY);
  });
});
