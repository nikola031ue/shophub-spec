import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DELETE_WARNING, ShopDelete } from './shop-delete';
import { Shop } from './shops-api';
import { shop, wait } from './testing';

describe('ShopDelete (FR-013)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ShopDelete],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function openForm(current: Shop = shop({ name: 'Acme Clothing' })) {
    const fixture = TestBed.createComponent(ShopDelete);
    fixture.componentRef.setInput('shop', current);
    const deleted: Shop[] = [];
    fixture.componentInstance.deleted.subscribe((s) => deleted.push(s));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector('button')!.click();
    await fixture.whenStable();
    return { fixture, el, deleted };
  }

  async function type(fixture: { whenStable(): Promise<unknown> }, el: HTMLElement, value: string) {
    const input = el.querySelector<HTMLInputElement>('input[name=confirmName]')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  const deleteButton = (el: HTMLElement) => el.querySelector<HTMLButtonElement>('button.danger')!;

  it('warns what is lost and asks for the name', async () => {
    const { el } = await openForm();
    expect(el.textContent).toContain(DELETE_WARNING);
    expect(el.textContent).toContain('Type Acme Clothing to confirm');
  });

  it('stays disabled until the typed name matches exactly (trimmed, same case)', async () => {
    const { fixture, el } = await openForm();
    expect(deleteButton(el).disabled).toBe(true);

    await type(fixture, el, 'acme clothing');
    expect(deleteButton(el).disabled).toBe(true);
    await type(fixture, el, 'Acme');
    expect(deleteButton(el).disabled).toBe(true);

    await type(fixture, el, '  Acme Clothing ');
    expect(deleteButton(el).disabled).toBe(false);
  });

  it('sends the trimmed name and reports the shop as being deleted', async () => {
    const { fixture, el, deleted } = await openForm();
    await type(fixture, el, ' Acme Clothing ');
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await wait(0);

    const req = http.expectOne({ method: 'DELETE', url: '/api/shops/s7k2m4qxab' });
    expect(req.request.body).toEqual({ confirmName: 'Acme Clothing' });
    req.flush(shop({ name: 'Acme Clothing', state: 'deleting', url: null }), { status: 202, statusText: 'Accepted' });
    await wait(0);
    await fixture.whenStable();

    expect(deleted.map((s) => s.state)).toEqual(['deleting']);
    expect(el.querySelector('form')).toBeNull();
  });

  it("shows the API's message when it refuses the name", async () => {
    const { fixture, el, deleted } = await openForm();
    await type(fixture, el, 'Acme Clothing');
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await wait(0);
    http.expectOne('/api/shops/s7k2m4qxab').flush(
      { title: 'One or more validation errors occurred.', errors: { confirmName: ["Type the shop's name exactly as shown to confirm."] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await wait(0);
    await fixture.whenStable();

    expect(el.querySelector('[role=alert]')?.textContent).toContain("Type the shop's name exactly as shown to confirm.");
    expect(deleted).toHaveLength(0);
  });

  it('cancel closes without a request', async () => {
    const { fixture, el } = await openForm();
    await type(fixture, el, 'Acme Clothing');
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Cancel'))!.click();
    await fixture.whenStable();

    expect(el.querySelector('form')).toBeNull();
    http.expectNone('/api/shops/s7k2m4qxab');
  });
});
