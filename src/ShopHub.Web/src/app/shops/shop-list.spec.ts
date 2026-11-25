import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { REFRESH_INTERVAL_MS, ShopList } from './shop-list';
import { Shop } from './shops-api';
import { preparing, shop, wait } from './testing';

const FAST = 40;

describe('ShopList (FR-008, FR-010)', () => {
  let http: HttpTestingController;

  function configure(interval?: number) {
    TestBed.configureTestingModule({
      imports: [ShopList],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        ...(interval === undefined ? [] : [{ provide: REFRESH_INTERVAL_MS, useValue: interval }]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  }

  afterEach(() => http.verify());

  async function render(shops: Shop[]) {
    const fixture = TestBed.createComponent(ShopList);
    fixture.detectChanges();
    http.expectOne({ method: 'GET', url: '/api/shops' }).flush({ shops });
    await wait(0);
    await fixture.whenStable();
    return fixture;
  }

  const el = (fixture: { nativeElement: unknown }) => fixture.nativeElement as HTMLElement;

  it('refreshes at least every 10 seconds by default (research R13: 5 s)', () => {
    configure();
    expect(TestBed.inject(REFRESH_INTERVAL_MS)).toBeLessThanOrEqual(10_000);
    expect(TestBed.inject(REFRESH_INTERVAL_MS)).toBe(5000);
  });

  it('reloads on its own while a shop is not ready, and stops once all are ready', async () => {
    configure(FAST);
    const fixture = await render([preparing()]);

    await wait(FAST * 2);
    http.expectOne('/api/shops').flush({ shops: [preparing()] });
    await wait(FAST * 2);
    http.expectOne('/api/shops').flush({ shops: [preparing({ state: 'ready', url: 'https://s2prep2222.shops.localhost', reason: null })] });
    await wait(0);
    await fixture.whenStable();
    expect(el(fixture).textContent).toContain('Ready');

    await wait(FAST * 4);
    http.expectNone('/api/shops');
  });

  it('does not poll when every shop is ready, and stops when the page is left', async () => {
    configure(FAST);
    const fixture = await render([shop()]);
    await wait(FAST * 3);
    http.expectNone('/api/shops');
    fixture.destroy();

    const again = await render([preparing()]);
    again.destroy();
    await wait(FAST * 3);
    http.expectNone('/api/shops');
  });

  it('keeps retrying after a failed load and shows the problem', async () => {
    configure(FAST);
    const fixture = TestBed.createComponent(ShopList);
    fixture.detectChanges();
    http.expectOne('/api/shops').flush({ title: 'x' }, { status: 503, statusText: 'Unavailable' });
    await wait(0);
    await fixture.whenStable();
    expect(el(fixture).textContent).toContain('temporarily unavailable');

    await wait(FAST * 2);
    http.expectOne('/api/shops').flush({ shops: [shop()] });
    await wait(0);
    await fixture.whenStable();
    expect(el(fixture).querySelector('[role=alert]')).toBeNull();
    fixture.destroy();
  });

  it('shows state, reason, address, availability, database and wallet', async () => {
    configure(60_000);
    const fixture = await render([shop({ availability: 'high', database: 'light' }), preparing()]);
    const [ready, prep] = Array.from(el(fixture).querySelectorAll<HTMLElement>('li.shop'));

    expect(ready.textContent).toContain('Acme');
    expect(ready.textContent).toContain('Ready');
    expect(ready.textContent).toContain('https://s7k2m4qxab.shops.localhost');
    expect(ready.textContent).toContain('High (3 copies)');
    expect(ready.textContent).toContain('Light (Redis)');
    expect(ready.textContent).toContain('0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed');
    expect(prep.textContent).toContain('Being prepared');
    expect(prep.textContent).toContain('The database is being prepared.');
    expect(prep.textContent).toContain('Not known yet');
    fixture.destroy();
  });

  it('opens only ready shops, in a new tab without access to the panel (rel noopener noreferrer)', async () => {
    configure(60_000);
    const fixture = await render([shop(), preparing(), shop({ id: 's2att22222', name: 'Old', state: 'attention', url: 'https://s2att22222.shops.localhost' })]);

    const links = Array.from(el(fixture).querySelectorAll<HTMLAnchorElement>('a[href]'));
    expect(links.length).toBeGreaterThan(0);
    for (const a of links) {
      expect(a.getAttribute('target')).toBe('_blank');
      expect(a.getAttribute('rel')).toBe('noopener noreferrer');
    }
    const items = Array.from(el(fixture).querySelectorAll<HTMLElement>('li.shop'));
    expect(items[0].querySelector('a.shop-name')?.getAttribute('href')).toBe('https://s7k2m4qxab.shops.localhost');
    expect(items[1].querySelector('a')).toBeNull();
    expect(items[2].querySelector('a')).toBeNull();
    // The admin password button only for ready shops.
    expect(items[0].querySelector('app-admin-password')).not.toBeNull();
    expect(items[1].querySelector('app-admin-password')).toBeNull();
    fixture.destroy();
  });

  it('offers "Change settings" and "Delete shop" except for shops being deleted, and reloads after a save', async () => {
    configure();
    const deleting = preparing({ id: 's3del33333', name: 'Old', state: 'deleting', reason: null });
    const fixture = await render([shop(), deleting]);
    const changeButton = (id: string) =>
      [...el(fixture).querySelectorAll(`[data-shop=${id}] button`)].find((b) => b.textContent?.includes('Change settings'));

    expect(changeButton('s7k2m4qxab')).toBeDefined();
    expect(changeButton('s3del33333')).toBeUndefined();
    const deleteButton = (id: string) =>
      [...el(fixture).querySelectorAll(`[data-shop=${id}] button`)].find((b) => b.textContent?.includes('Delete shop'));
    expect(deleteButton('s7k2m4qxab')).toBeDefined();
    expect(deleteButton('s3del33333')).toBeUndefined();

    (changeButton('s7k2m4qxab') as HTMLButtonElement).click();
    await fixture.whenStable();
    const select = el(fixture).querySelector<HTMLSelectElement>('[data-shop=s7k2m4qxab] select[name=availability]')!;
    select.value = 'high';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    el(fixture).querySelector('[data-shop=s7k2m4qxab] form')!.dispatchEvent(new Event('submit'));
    await wait(0);
    http.expectOne({ method: 'PATCH', url: '/api/shops/s7k2m4qxab' }).flush(shop({ availability: 'high' }));
    await wait(0);

    http.expectOne({ method: 'GET', url: '/api/shops' }).flush({ shops: [shop({ availability: 'high' }), deleting] });
    await wait(0);
    await fixture.whenStable();
    expect(el(fixture).querySelector('[data-shop=s7k2m4qxab]')!.textContent).toContain('High (3 copies)');
    fixture.destroy();
  });

  it('says so when there are no shops yet', async () => {
    configure(60_000);
    const fixture = await render([]);
    expect(el(fixture).textContent).toContain('You have no shops yet.');
  });
});
