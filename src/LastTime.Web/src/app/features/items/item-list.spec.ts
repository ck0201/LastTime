import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
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

  describe('long descriptions', () => {
    const longText = 'A very long description that goes on and on. '.repeat(60); // far more than 3 lines
    const lines = (n: number) => n * 24;

    // jsdom has no layout, so say which clamped descriptions "do not fit": the ones with long text.
    let fits: (el: HTMLElement) => boolean;
    beforeEach(() => {
      fits = (el) => (el.textContent ?? '').length < 200;
      vi.spyOn(Element.prototype, 'clientHeight', 'get').mockImplementation(() => lines(3));
      vi.spyOn(Element.prototype, 'scrollHeight', 'get').mockImplementation(function (this: Element) {
        return fits(this as HTMLElement) ? lines(2) : lines(40);
      });
    });
    afterEach(() => vi.restoreAllMocks());

    const rows: ItemSummary[] = [
      { id: 'long', name: 'Warehouse', description: longText, createdAt: '2026-09-30T00:00:00Z', lastEntry: null },
      { id: 'short', name: 'Bike', description: 'Red, 21 gears', createdAt: '2026-09-30T00:00:00Z', lastEntry: null },
      { id: 'none', name: 'Laptop', description: null, createdAt: '2026-09-30T00:00:00Z', lastEntry: null },
    ];

    const wrap = (root: HTMLElement, name: string) =>
      Array.from(root.querySelectorAll<HTMLElement>('li.card-wrap')).find((li) => li.textContent!.includes(name))!;
    const desc = (li: HTMLElement) => li.querySelector<HTMLElement>('.card-sub');
    const more = (li: HTMLElement) => li.querySelector<HTMLButtonElement>('button.card-more');
    const settle = () => TestBed.inject(ApplicationRef).whenStable();

    it('leaves short descriptions unchanged, with no Read more', async () => {
      const root = await render(rows);
      const li = wrap(root, 'Bike');
      expect(desc(li)!.textContent?.trim()).toBe('Red, 21 gears');
      expect(more(li)).toBeNull();
      expect(li.querySelector('.card-has-more')).toBeNull();
    });

    it('truncates a long description and offers Read more', async () => {
      const root = await render(rows);
      const li = wrap(root, 'Warehouse');

      expect(desc(li)!.classList.contains('card-sub-clamped')).toBe(true);
      expect(more(li)!.textContent?.trim()).toBe('Read more');
      expect(more(li)!.getAttribute('aria-expanded')).toBe('false');
      expect(more(li)!.getAttribute('aria-controls')).toBe(desc(li)!.id);
      expect(more(li)!.getAttribute('aria-label')).toBe('Read more: Warehouse');
      expect(desc(li)!.textContent?.trim()).toBe(longText.trim()); // the data is untouched; only the display is clamped
    });

    it('Read more reveals the full description inside the same card, and Read less collapses it', async () => {
      const root = await render(rows);
      const li = wrap(root, 'Warehouse');

      more(li)!.click();
      await settle();
      expect(desc(li)!.classList.contains('card-sub-clamped')).toBe(false);
      expect(desc(li)!.textContent?.trim()).toBe(longText.trim());
      expect(more(li)!.textContent?.trim()).toBe('Read less');
      expect(more(li)!.getAttribute('aria-expanded')).toBe('true');
      expect(more(li)!.getAttribute('aria-label')).toBe('Read less: Warehouse');
      expect(li.querySelector('a.card')!.contains(desc(li))).toBe(true); // still within the card

      more(li)!.click();
      await settle();
      expect(desc(li)!.classList.contains('card-sub-clamped')).toBe(true);
      expect(more(li)!.textContent?.trim()).toBe('Read more');
      expect(more(li)!.getAttribute('aria-expanded')).toBe('false');
    });

    it('keeps the button outside the card link and does not navigate when it is used', async () => {
      const root = await render(rows);
      const li = wrap(root, 'Warehouse');
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

      expect(li.querySelector('a.card')!.contains(more(li))).toBe(false); // no button nested inside a link
      more(li)!.click();
      await settle();
      expect(navigate).not.toHaveBeenCalled();
    });

    it('expands cards independently', async () => {
      const root = await render([
        ...rows,
        { id: 'long2', name: 'Garage', description: longText, createdAt: '2026-09-30T00:00:00Z', lastEntry: null },
      ]);

      more(wrap(root, 'Warehouse'))!.click();
      await settle();

      expect(desc(wrap(root, 'Warehouse'))!.classList.contains('card-sub-clamped')).toBe(false);
      expect(desc(wrap(root, 'Garage'))!.classList.contains('card-sub-clamped')).toBe(true);
      expect(more(wrap(root, 'Garage'))!.textContent?.trim()).toBe('Read more');
    });

    it('treats a missing description exactly as before: no description line, no button', async () => {
      const root = await render(rows);
      const li = wrap(root, 'Laptop');
      expect(desc(li)).toBeNull();
      expect(more(li)).toBeNull();
      expect(li.querySelector('.card-title')!.textContent).toContain('Laptop');
      expect(li.querySelector('.card-meta')!.textContent).toContain('No history yet');
    });

    it('re-checks when the window is resized, so Read more appears or goes away with the layout', async () => {
      const root = await render(rows);
      const li = wrap(root, 'Warehouse');
      expect(more(li)).not.toBeNull();

      fits = () => true; // e.g. rotated to a wider screen
      window.dispatchEvent(new Event('resize'));
      await settle();
      expect(more(li)).toBeNull();

      fits = (el) => (el.textContent ?? '').length < 200; // and back to narrow
      window.dispatchEvent(new Event('resize'));
      await settle();
      expect(more(li)).not.toBeNull();
    });

    it('has no Read more when nothing is cut off, even for a long description on a wide screen', async () => {
      fits = () => true;
      const root = await render(rows);
      expect(root.querySelectorAll('button.card-more').length).toBe(0);
    });
  });
});
