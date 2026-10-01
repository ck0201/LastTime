import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
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

  describe('"when did I last…?" lookup', () => {
    async function renderWithHttp() {
      const root = await render(detail);
      return { root, http: TestBed.inject(HttpTestingController) };
    }

    it('offers each distinct action once as a shortcut', async () => {
      const root = await render({
        ...detail,
        history: [
          { ...detail.history[0], id: 'a', action: 'Serviced' },
          { ...detail.history[0], id: 'b', action: 'serviced' },
          { ...detail.history[1], id: 'c', action: 'Purchased' },
        ],
      });
      const chips = Array.from(root.querySelectorAll('.chip')).map((c) => c.textContent?.trim());
      expect(chips).toEqual(['Serviced', 'Purchased']);
    });

    it('answers with the date, cost and provider when a shortcut is clicked', async () => {
      const { root, http } = await renderWithHttp();

      root.querySelector<HTMLButtonElement>('.chip')!.click();
      const req = http.expectOne((r) => r.url === '/api/items/i1/history/last');
      expect(req.request.params.get('action')).toBe('Serviced');
      req.flush(detail.history[0]);
      await TestBed.inject(ApplicationRef).whenStable();

      const answer = root.querySelector('.answer')!.textContent!.replace(/\s+/g, ' ');
      expect(answer).toContain('12 Aug 2026');
      expect(answer).toContain('₹850');
      expect(answer).toContain('CoolCare Services');
      expect(answer).toContain('Gas checked');
    });

    it('says so when nothing matches', async () => {
      const { root, http } = await renderWithHttp();

      const input = root.querySelector<HTMLInputElement>('.lookup input')!;
      input.value = 'painted';
      input.dispatchEvent(new Event('input'));
      await TestBed.inject(ApplicationRef).whenStable();
      root.querySelector<HTMLFormElement>('.lookup form')!.dispatchEvent(new Event('submit'));

      http.expectOne((r) => r.url === '/api/items/i1/history/last')
        .flush('none', { status: 404, statusText: 'Not Found' });
      await TestBed.inject(ApplicationRef).whenStable();

      expect(root.querySelector('.lookup')!.textContent).toContain('Nothing on record matching “painted”');
    });

    describe('shortcut suggestions', () => {
      const actions = ['Service', 'service', 'Repair', 'AC Service', 'Bike Service', 'Cleaning', 'Maintenance', 'Inspection', 'SERVICE'];

      async function renderActions(list: string[]) {
        return render({
          ...detail,
          history: list.map((action, i) => ({ ...detail.history[0], id: `h${i}`, action })),
        });
      }

      async function type(root: HTMLElement, text: string) {
        const input = root.querySelector<HTMLInputElement>('.lookup input')!;
        input.value = text;
        input.dispatchEvent(new Event('input'));
        await TestBed.inject(ApplicationRef).whenStable();
      }

      const chips = (root: HTMLElement) =>
        Array.from(root.querySelectorAll('.chip')).map((c) => c.textContent?.trim());
      const more = (root: HTMLElement) => root.querySelector('.chip-more');

      it('deduplicates case-insensitively, keeping the first occurrence casing', async () => {
        const root = await renderActions(['service', 'Service', 'SERVICE', 'Repair']);
        expect(chips(root)).toEqual(['service', 'Repair']);
      });

      it('filters by the typed text (case-insensitive, contains), deduplicated', async () => {
        const root = await renderActions(actions);
        await type(root, 'SER');
        expect(chips(root)).toEqual(['Service', 'AC Service', 'Bike Service']);
        expect(more(root)).toBeNull();
      });

      it('shows at most 5 suggestions followed by "..." when more than 5 unique match', async () => {
        const root = await renderActions(['a1', 'a2', 'a3', 'a4', 'a5', 'a6', 'a7']);
        expect(chips(root)).toEqual(['a1', 'a2', 'a3', 'a4', 'a5']);
        expect(more(root)?.textContent?.trim()).toBe('...');

        await type(root, 'a');
        expect(chips(root).length).toBe(5);
        expect(more(root)).not.toBeNull();
      });

      it('does not show "..." for exactly 5 unique matches or fewer', async () => {
        const root = await renderActions(['a1', 'a2', 'a3', 'a4', 'a5', 'A5']);
        expect(chips(root)).toEqual(['a1', 'a2', 'a3', 'a4', 'a5']);
        expect(more(root)).toBeNull();
      });

      it('"..." is not selectable: it is not a button and does nothing when clicked', async () => {
        const root = await renderActions(['a1', 'a2', 'a3', 'a4', 'a5', 'a6']);
        const dots = more(root) as HTMLElement;

        expect(dots.tagName).not.toBe('BUTTON');
        expect(dots.classList.contains('chip')).toBe(false);
        dots.click();
        TestBed.inject(HttpTestingController).expectNone((r) => r.url.includes('/history/last'));
      });

      it('shows nothing (no chips, no "...") when nothing matches the typed text', async () => {
        const root = await renderActions(actions);
        await type(root, 'zzz');
        expect(chips(root)).toEqual([]);
        expect(more(root)).toBeNull();
      });

      it('still looks up using the typed text, regardless of the suggestions', async () => {
        const root = await renderActions(actions);
        await type(root, 'ser');
        root.querySelector<HTMLFormElement>('.lookup form')!.dispatchEvent(new Event('submit'));

        const req = TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/items/i1/history/last');
        expect(req.request.params.get('action')).toBe('ser');
        req.flush(detail.history[0]);
      });
    });

    it('does not offer the lookup when the item has no history', async () => {
      const root = await render({ ...detail, history: [] });
      expect(root.querySelector('.lookup')).toBeNull();
    });
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
