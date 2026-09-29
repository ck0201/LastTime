import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ItemsApi } from './items-api';

describe('ItemsApi', () => {
  let api: ItemsApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ItemsApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists items without a search param when the term is blank', () => {
    api.list('   ').subscribe();
    const req = http.expectOne('/api/items');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.has('search')).toBe(false);
    req.flush([]);
  });

  it('sends the trimmed search term', () => {
    api.list(' ac ').subscribe();
    const req = http.expectOne((r) => r.url === '/api/items');
    expect(req.request.params.get('search')).toBe('ac');
    req.flush([]);
  });

  it('last() returns the entry, and null when the API says 404', () => {
    let result: unknown = 'unset';
    api.last('item-1', ' service ').subscribe((r) => (result = r));
    const hit = http.expectOne((r) => r.url === '/api/items/item-1/history/last');
    expect(hit.request.params.get('action')).toBe('service');
    hit.flush({ id: 'e1', action: 'Serviced' });
    expect(result).toEqual({ id: 'e1', action: 'Serviced' });

    api.last('item-1', 'painted').subscribe((r) => (result = r));
    http.expectOne((r) => r.url === '/api/items/item-1/history/last')
      .flush('none', { status: 404, statusText: 'Not Found' });
    expect(result).toBeNull();
  });

  it('last() still surfaces real errors', () => {
    let failed = false;
    api.last('item-1', 'service').subscribe({ error: () => (failed = true) });
    http.expectOne((r) => r.url === '/api/items/item-1/history/last')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    expect(failed).toBe(true);
  });

  it('posts a new history entry under the item', () => {
    const body = { date: '2026-08-12', action: 'Serviced', cost: 850, provider: null, notes: null };
    api.addEntry('item-1', body).subscribe();
    const req = http.expectOne('/api/items/item-1/history');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({});
  });

  it('updates and deletes entries by item and entry id', () => {
    const body = { date: '2026-08-12', action: 'Serviced', cost: null, provider: null, notes: null };
    api.updateEntry('item-1', 'e-1', body).subscribe();
    const put = http.expectOne('/api/items/item-1/history/e-1');
    expect(put.request.method).toBe('PUT');
    put.flush({});

    api.deleteEntry('item-1', 'e-1').subscribe();
    const del = http.expectOne('/api/items/item-1/history/e-1');
    expect(del.request.method).toBe('DELETE');
    del.flush(null);
  });
});
