import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ItemSummary } from '../../core/models';
import { ItemList } from './item-list';

describe('ItemList', () => {
  const items: ItemSummary[] = [
    {
      id: 'i1', name: 'LG AC', description: 'Bedroom', createdAt: '2026-09-30T00:00:00Z',
      lastEntry: {
        id: 'e1', itemId: 'i1', date: '2026-08-12', action: 'Serviced', cost: 850,
        provider: 'CoolCare Services', notes: null, createdAt: '2026-08-12T10:00:00Z',
      },
    },
    { id: 'i2', name: 'Bike', description: null, createdAt: '2026-09-30T00:00:00Z', lastEntry: null },
  ];

  async function render(response: ItemSummary[] | 'error') {
    await TestBed.configureTestingModule({
      imports: [ItemList],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    const fixture = TestBed.createComponent(ItemList);
    fixture.detectChanges();
    await new Promise((r) => setTimeout(r, 300)); // search is debounced by 250ms

    const req = TestBed.inject(HttpTestingController).expectOne('/api/items');
    if (response === 'error') req.flush('boom', { status: 500, statusText: 'Server Error' });
    else req.flush(response);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it("shows each item's last entry, or that it has no history", async () => {
    const root = await render(items);
    const cards = Array.from(root.querySelectorAll('.card')).map((c) => c.textContent!.replace(/\s+/g, ' '));

    expect(cards[0]).toContain('LG AC');
    expect(cards[0]).toContain('Last: Serviced');
    expect(cards[0]).toContain('12 Aug 2026');
    expect(cards[0]).toContain('₹850');
    expect(cards[1]).toContain('Bike');
    expect(cards[1]).toContain('No history yet');
  });

  it('shows the welcome state when there are no items at all', async () => {
    const root = await render([]);
    expect(root.textContent).toContain('Nothing here yet');
  });

  it('shows an error when the API is unreachable', async () => {
    const root = await render('error');
    expect(root.textContent).toContain("Couldn't load items");
  });
});
