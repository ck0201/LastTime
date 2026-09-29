import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ItemDetail } from '../../core/models';
import { ItemDetailPage } from './item-detail';

describe('ItemDetailPage', () => {
  const detail: ItemDetail = {
    id: 'i1',
    name: 'LG AC',
    description: 'Bedroom',
    createdAt: '2026-09-30T00:00:00Z',
    history: [
      { id: 'e2', itemId: 'i1', date: '2026-08-12', action: 'Serviced', cost: 850,
        provider: 'CoolCare Services', notes: 'Gas checked', createdAt: '2026-08-12T10:00:00Z' },
      { id: 'e1', itemId: 'i1', date: '2024-03-14', action: 'Purchased', cost: 38999,
        provider: null, notes: null, createdAt: '2024-03-14T10:00:00Z' },
    ],
  };

  async function render(response: ItemDetail | null) {
    await TestBed.configureTestingModule({
      imports: [ItemDetailPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    const fixture = TestBed.createComponent(ItemDetailPage);
    fixture.componentRef.setInput('id', 'i1');
    fixture.detectChanges();

    const req = TestBed.inject(HttpTestingController).expectOne('/api/items/i1');
    if (response) req.flush(response);
    else req.flush('nope', { status: 404, statusText: 'Not Found' });
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders the timeline in the order the API returns it, with cost, provider and notes', async () => {
    const root = await render(detail);

    expect(root.querySelector('h1')?.textContent).toContain('LG AC');
    const actions = Array.from(root.querySelectorAll('.entry-action')).map((e) => e.textContent?.trim());
    expect(actions).toEqual(['Serviced', 'Purchased']);

    const text = root.textContent!;
    expect(text).toContain('₹850');
    expect(text).toContain('₹38,999');
    expect(text).toContain('12 Aug 2026');
    expect(text).toContain('CoolCare Services');
    expect(text).toContain('Gas checked');
  });

  it('shows an empty state when there is no history', async () => {
    const root = await render({ ...detail, history: [] });
    expect(root.textContent).toContain('No history yet');
  });

  it('shows an error when the item cannot be loaded', async () => {
    const root = await render(null);
    expect(root.textContent).toContain("Couldn't find this item");
  });
});
