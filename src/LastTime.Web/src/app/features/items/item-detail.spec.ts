import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
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

  describe('item header', () => {
    it('shows a back link to the item list, the item name and its description', async () => {
      const root = await render(detail);
      const bar = root.querySelector('header.item-bar')!;

      const back = bar.querySelector<HTMLAnchorElement>('a.item-back')!;
      expect(back.getAttribute('href')).toBe('/');
      expect(back.getAttribute('aria-label')).toBe('Back to all items');
      expect(bar.querySelector('h1')!.textContent).toContain('LG AC');
      expect(bar.querySelector('p')!.textContent).toContain('Bedroom');
    });

    it('omits the description line when the item has none', async () => {
      const root = await render({ ...detail, description: null });
      const bar = root.querySelector('header.item-bar')!;
      expect(bar.querySelector('h1')!.textContent).toContain('LG AC');
      expect(bar.querySelector('p')).toBeNull();
    });

    it('keeps the back link when the item cannot be loaded', async () => {
      const root = await render(null);
      expect(root.querySelector('header.item-bar a.item-back')).not.toBeNull();
      expect(root.querySelector('header.item-bar h1')).toBeNull();
      expect(root.querySelector('.notice-error')!.textContent).toContain("Couldn't find this item");
    });

    describe('item actions menu', () => {
      const settle = () => TestBed.inject(ApplicationRef).whenStable();
      const toggle = (root: HTMLElement) => root.querySelector<HTMLButtonElement>('.item-menu-toggle')!;
      const menu = (root: HTMLElement) => root.querySelector<HTMLElement>('#item-menu');
      const items = (root: HTMLElement) =>
        Array.from(menu(root)!.querySelectorAll('.item-menu-item')).map((i) => i.textContent?.trim());
      async function open(root: HTMLElement) {
        toggle(root).click();
        await settle();
      }

      it('replaces the separate Edit/Delete row: no row below the header, one menu button inside it', async () => {
        const root = await render(detail);
        expect(root.querySelector('.item-actions')).toBeNull();
        expect(root.querySelector('.page-head')).toBeNull();
        // No Edit or Delete button is rendered outside the (closed) menu.
        const stray = Array.from(root.querySelectorAll('.btn')).filter((b) => /^(Edit|Delete)/.test(b.textContent!.trim()));
        expect(stray).toEqual([]);

        const button = toggle(root);
        expect(root.querySelector('header.item-bar')!.contains(button)).toBe(true);
        expect(button.getAttribute('aria-label')).toBe('Item actions');
        expect(button.getAttribute('aria-expanded')).toBe('false');
        expect(button.getAttribute('aria-controls')).toBe('item-menu');
        expect(menu(root)).toBeNull();
      });

      it('has no menu while loading or when the item cannot be loaded', async () => {
        const root = await render(null);
        expect(root.querySelector('.item-menu-toggle')).toBeNull();
      });

      it('opens with Edit, Export (PDF), and a destructive Delete, and the toggle reports it is expanded', async () => {
        const root = await render(detail);
        await open(root);

        expect(toggle(root).getAttribute('aria-expanded')).toBe('true');
        expect(items(root)).toEqual(['Edit', 'Export (PDF)', 'Delete']);
        const edit = menu(root)!.querySelector('a.item-menu-item')!;
        expect(edit.getAttribute('href')).toBe('/items/i1/edit');
        const del = menu(root)!.querySelector('button.item-menu-danger')!;
        expect(del).not.toBeNull();
      });

      it('closes when the button is pressed again, on Escape, and on an outside click', async () => {
        const root = await render(detail);

        await open(root);
        toggle(root).click();
        await settle();
        expect(menu(root)).toBeNull();

        await open(root);
        toggle(root).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await settle();
        expect(menu(root)).toBeNull();
        expect(toggle(root).getAttribute('aria-expanded')).toBe('false');

        await open(root);
        root.querySelector<HTMLElement>('.item-menu-backdrop')!.click();
        await settle();
        expect(menu(root)).toBeNull();
      });

      it('Edit goes to the existing edit page and closes the menu', async () => {
        const root = await render(detail);
        await open(root);
        const router = TestBed.inject(Router);
        const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true); // the test router has no routes
        const edit = menu(root)!.querySelector<HTMLAnchorElement>('a.item-menu-item')!;
        edit.click();
        await settle();

        expect(navigate).toHaveBeenCalledTimes(1);
        expect(router.serializeUrl(navigate.mock.calls[0][0] as UrlTree)).toBe('/items/i1/edit');
        expect(menu(root)).toBeNull();
      });

      it('Export (PDF) triggers window.print and closes the menu', async () => {
        const root = await render(detail);
        await open(root);

        const printSpy = vi.spyOn(window, 'print').mockImplementation(() => {});
        const exportBtn = Array.from(menu(root)!.querySelectorAll<HTMLButtonElement>('button.item-menu-item'))
          .find((b) => b.textContent?.trim() === 'Export (PDF)')!;
        expect(exportBtn).not.toBeNull();

        exportBtn.click();
        await settle();

        expect(printSpy).toHaveBeenCalledTimes(1);
        expect(menu(root)).toBeNull();
        printSpy.mockRestore();
      });

      it('Export (PDF) sets document title during print and restores it after', async () => {
        const root = await render(detail);
        await open(root);

        const printSpy = vi.spyOn(window, 'print').mockImplementation(() => {});
        const originalTitle = document.title;

        const exportBtn = Array.from(menu(root)!.querySelectorAll<HTMLButtonElement>('button.item-menu-item'))
          .find((b) => b.textContent?.trim() === 'Export (PDF)')!;
        exportBtn.click();
        await settle();

        expect(document.title).toBe('LG AC - History Report - Last Time');

        window.dispatchEvent(new Event('afterprint'));
        expect(document.title).toBe(originalTitle);

        printSpy.mockRestore();
      });

      it('Delete asks for confirmation inside the menu, and confirming deletes the item', async () => {
        const root = await render(detail);
        const http = TestBed.inject(HttpTestingController);
        await open(root);

        menu(root)!.querySelector<HTMLButtonElement>('button.item-menu-danger')!.click(); // Delete
        await settle();
        http.expectNone((r) => r.method === 'DELETE'); // nothing is deleted by the first tap
        expect(items(root)).toEqual(['Delete item and history?', 'Keep']);
        expect(menu(root)!.querySelector('.item-menu-danger')!.textContent).toContain('Delete item and history?');

        menu(root)!.querySelector<HTMLButtonElement>('.item-menu-danger')!.click();
        const req = http.expectOne((r) => r.method === 'DELETE' && r.url === '/api/items/i1');
        req.flush(null, { status: 204, statusText: 'No Content' });
        await settle();
      });

      it('Keep closes the menu without deleting, and reopening starts again at Edit / Export / Delete', async () => {
        const root = await render(detail);
        const http = TestBed.inject(HttpTestingController);
        await open(root);
        menu(root)!.querySelector<HTMLButtonElement>('button.item-menu-danger')!.click(); // Delete
        await settle();

        Array.from(menu(root)!.querySelectorAll<HTMLButtonElement>('button.item-menu-item'))
          .find((b) => b.textContent?.trim() === 'Keep')!
          .click();
        await settle();
        expect(menu(root)).toBeNull();
        http.expectNone((r) => r.method === 'DELETE');

        await open(root);
        expect(items(root)).toEqual(['Edit', 'Export (PDF)', 'Delete']);
      });

      it('closing the menu with Escape abandons a half-finished delete confirmation', async () => {
        const root = await render(detail);
        await open(root);
        menu(root)!.querySelector<HTMLButtonElement>('button.item-menu-danger')!.click(); // Delete
        await settle();

        toggle(root).dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
        await settle();
        await open(root);
        expect(items(root)).toEqual(['Edit', 'Export (PDF)', 'Delete']);
      });

      it('shows a delete failure below the header', async () => {
        const root = await render(detail);
        const http = TestBed.inject(HttpTestingController);
        await open(root);
        menu(root)!.querySelector<HTMLButtonElement>('button.item-menu-danger')!.click();
        await settle();
        menu(root)!.querySelector<HTMLButtonElement>('.item-menu-danger')!.click();
        http.expectOne((r) => r.method === 'DELETE').flush('no', { status: 500, statusText: 'Server Error' });
        await settle();

        expect(root.querySelector('.notice-error')!.textContent).toContain("Couldn't delete the item.");
      });
    });

    describe('printable report', () => {
      it('renders the item details and all history entries in a print table', async () => {
        const root = await render(detail);
        const report = root.querySelector('.print-report')!;
        expect(report).not.toBeNull();
        expect(report.getAttribute('aria-hidden')).toBe('true');

        expect(report.querySelector('.print-title')?.textContent).toContain('LG AC');
        expect(report.querySelector('.print-desc')?.textContent).toContain('Bedroom');
        expect(report.querySelector('.print-meta')?.textContent).toContain('2 activities');
        expect(report.querySelector('.print-meta')?.textContent).toContain('Total spending: ₹39,849');

        const rows = Array.from(report.querySelectorAll('.print-table tbody tr'));
        expect(rows.length).toBe(2);

        // First row (Serviced)
        expect(rows[0].querySelector('.col-date')?.textContent).toContain('12 Aug 2026');
        expect(rows[0].querySelector('.col-action')?.textContent).toContain('Serviced');
        expect(rows[0].querySelector('.col-provider')?.textContent).toContain('CoolCare Services');
        expect(rows[0].querySelector('.col-cost')?.textContent).toContain('₹850');
        expect(rows[0].querySelector('.col-notes')?.textContent).toContain('Gas checked');

        // Second row (Purchased)
        expect(rows[1].querySelector('.col-date')?.textContent).toContain('14 Mar 2024');
        expect(rows[1].querySelector('.col-action')?.textContent).toContain('Purchased');
        expect(rows[1].querySelector('.col-provider')?.textContent).toContain('—');
        expect(rows[1].querySelector('.col-cost')?.textContent).toContain('₹38,999');
        expect(rows[1].querySelector('.col-notes')?.textContent).toContain('—');
      });

      it('shows empty message when item has no history', async () => {
        const root = await render({ ...detail, history: [] });
        const report = root.querySelector('.print-report')!;
        expect(report.querySelector('.print-table')).toBeNull();
        expect(report.querySelector('.print-empty')?.textContent).toContain('No history recorded for this item.');
      });
    });

    it('has a single h1, so the item name is the page heading', async () => {
      const root = await render(detail);
      expect(root.querySelectorAll('h1').length).toBe(1);
    });
  });

  describe('insights', () => {
    type Row = { action: string; cost?: number | null; date?: string };

    async function renderRows(rows: Row[]) {
      return render({
        ...detail,
        history: rows.map((r, i) => ({
          ...detail.history[0],
          id: `h${i}`,
          action: r.action,
          cost: r.cost ?? null,
          date: r.date ?? '2026-08-12',
        })),
      });
    }

    const summary = (root: HTMLElement) =>
      Array.from(root.querySelectorAll('.insight-list li')).map((li) =>
        Array.from(li.children).map((c) => c.textContent?.trim()),
      );
    const insightsText = (root: HTMLElement) => root.querySelector('.insights')!.textContent!.replace(/\s+/g, ' ');

    it('groups actions case-insensitively (a known word shows as its base word)', async () => {
      const root = await renderRows([{ action: 'service' }, { action: 'Service' }, { action: 'SERVICE' }, { action: 'Repair' }]);
      expect(summary(root)).toEqual([['Service', '3'], ['Repair', '1']]);
    });

    it('sorts by count descending', async () => {
      const root = await renderRows([
        { action: 'Repair' }, { action: 'Service' }, { action: 'service' }, { action: 'Cleaning' },
        { action: 'Cleaning' }, { action: 'Cleaning' },
      ]);
      expect(summary(root)).toEqual([['Cleaning', '3'], ['Service', '2'], ['Repair', '1']]);
    });

    it('keeps first-seen order when counts tie', async () => {
      const root = await renderRows([{ action: 'B' }, { action: 'A' }, { action: 'C' }, { action: 'a' }, { action: 'b' }]);
      expect(summary(root)).toEqual([['B', '2'], ['A', '2'], ['C', '1']]);
    });

    it('ignores null costs and includes zero costs', async () => {
      const root = await renderRows([
        { action: 'a', cost: 100 }, { action: 'b', cost: null }, { action: 'c', cost: 0 }, { action: 'd', cost: 50.5 },
      ]);
      expect(insightsText(root)).toContain('₹150.50');
    });

    it('treats an entry costing 0 as a recorded cost', async () => {
      const root = await renderRows([{ action: 'a', cost: 0 }, { action: 'b', cost: null }]);
      expect(insightsText(root)).toContain('₹0');
      expect(insightsText(root)).not.toContain('No costs recorded');
    });

    it('rounds the total to 2 decimals (no floating-point drift)', async () => {
      const root = await renderRows([{ action: 'a', cost: 0.1 }, { action: 'b', cost: 0.2 }]);
      expect(root.querySelector('.insight-value')!.textContent?.trim()).toBe('₹0.30');
    });

    it('formats whole-rupee totals without decimals', async () => {
      const root = await renderRows([{ action: 'a', cost: 38999 }, { action: 'b', cost: 850 }]);
      expect(root.querySelector('.insight-value')!.textContent?.trim()).toBe('₹39,849');
    });

    it('says "No costs recorded" when every cost is null', async () => {
      const root = await renderRows([{ action: 'a' }, { action: 'b' }]);
      expect(insightsText(root)).toContain('No costs recorded');
      expect(root.querySelector('.insight-value')).toBeNull();
    });

    it('shows the first history entry as the last activity, with date and relative date', async () => {
      const root = await renderRows([
        { action: 'Newest', date: '2026-08-12' },
        { action: 'Older', date: '2024-03-14' },
      ]);
      const last = root.querySelector('.insights-grid > div:nth-child(3)')!.textContent!;
      expect(last).toContain('Last activity');
      expect(last).toContain('Newest');
      expect(last).toContain('12 Aug 2026');
      expect(last).toMatch(/\(.+ago\)/);
      expect(last).not.toContain('Older');
    });

    it('appears above the lookup section and the timeline', async () => {
      const root = await renderRows([{ action: 'a' }]);
      const insights = root.querySelector('.insights')!;
      expect(insights.compareDocumentPosition(root.querySelector('.lookup')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(insights.compareDocumentPosition(root.querySelector('.timeline')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    });

    it('is hidden when the item has no history', async () => {
      const root = await render({ ...detail, history: [] });
      expect(root.querySelector('.insights')).toBeNull();
    });
  });

  describe('typical gap', () => {
    type Row = { action: string; date: string };

    async function renderRows(rows: Row[]) {
      return render({
        ...detail,
        history: rows.map((r, i) => ({ ...detail.history[0], id: `g${i}`, action: r.action, date: r.date })),
      });
    }

    const on = (action: string, ...dates: string[]): Row[] => dates.map((date) => ({ action, date }));
    const gaps = (root: HTMLElement) =>
      Array.from(root.querySelectorAll('.gap-list li')).map((li) =>
        Array.from(li.children).map((c) => c.textContent?.trim()),
      );
    const summary = (root: HTMLElement) => root.querySelectorAll('.insight-list li').length;

    it('is not shown with fewer than 3 distinct dates', async () => {
      const root = await renderRows(on('Service', '2025-01-01', '2025-05-01'));
      expect(root.querySelector('.gap-list')).toBeNull();
      expect(root.textContent).not.toContain('Typical gap');
    });

    it('is calculated for exactly 3 distinct dates', async () => {
      const root = await renderRows(on('Service', '2025-01-01', '2025-05-01', '2025-09-01'));
      expect(gaps(root)).toEqual([['Service', 'every ~4 months']]);
    });

    it('counts a date once when the same action is logged twice that day', async () => {
      const twoDates = await renderRows(on('Service', '2025-01-01', '2025-01-01', '2025-05-01'));
      expect(twoDates.querySelector('.gap-list')).toBeNull();

      TestBed.resetTestingModule();
      const threeDates = await renderRows(on('Service', '2025-01-01', '2025-01-01', '2025-05-01', '2025-09-01'));
      expect(gaps(threeDates)).toEqual([['Service', 'every ~4 months']]);
    });

    it('groups actions case-insensitively and keeps the first-seen casing', async () => {
      const root = await renderRows([
        { action: 'Service', date: '2025-01-01' },
        { action: 'service', date: '2025-05-01' },
        { action: 'SERVICE', date: '2025-09-01' },
      ]);
      expect(gaps(root)).toEqual([['Service', 'every ~4 months']]);
    });

    it('ignores future-dated entries', async () => {
      const onlyTwoPast = await renderRows(on('Service', '2025-01-01', '2025-05-01', '2999-01-01'));
      expect(onlyTwoPast.querySelector('.gap-list')).toBeNull();

      TestBed.resetTestingModule();
      const threePast = await renderRows(on('Service', '2025-01-01', '2025-05-01', '2025-09-01', '2999-01-01'));
      expect(gaps(threePast)).toEqual([['Service', 'every ~4 months']]);
    });

    it('uses the mean of the gaps when they are uneven', async () => {
      // Gaps of 10, 10 and 100 days: mean 40 days (~6 weeks); the typical (median) gap would be much shorter.
      const root = await renderRows(on('Oil change', '2025-01-01', '2025-01-11', '2025-01-21', '2025-05-01'));
      expect(gaps(root)).toEqual([['Oil change', 'every ~6 weeks']]);
    });

    it('shows very short intervals as daily, never "~0 days"', async () => {
      const root = await renderRows(on('Medicine', '2025-01-01', '2025-01-02', '2025-01-03'));
      expect(gaps(root)).toEqual([['Medicine', 'daily']]);
    });

    it('caps at 3 actions, in the same order as the activity summary', async () => {
      const d = ['2025-01-01', '2025-02-01', '2025-03-01'];
      // Entry counts D=3, C=4, B=5, A=6 (extras repeat a date), logged D first.
      const many = (action: string, n: number) => Array.from({ length: n }, (_, i) => ({ action, date: d[i % 3] }));
      const root = await renderRows([...many('D', 3), ...many('C', 4), ...many('B', 5), ...many('A', 6)]);

      expect(summary(root)).toBe(4);
      expect(gaps(root).map((g) => g[0])).toEqual(['A', 'B', 'C']);
    });

    it('does not use forecast wording', async () => {
      const root = await renderRows(on('Service', '2025-01-01', '2025-05-01', '2025-09-01'));
      const text = root.querySelector('.gap-list')!.parentElement!.textContent!;
      expect(text).not.toMatch(/next|due|expected|will/i);
    });

    it('leaves the existing insights unchanged', async () => {
      const root = await renderRows(on('Service', '2025-01-01', '2025-05-01', '2025-09-01'));
      const labels = Array.from(root.querySelectorAll('.insight-label')).map((l) => l.textContent?.trim());
      expect(labels).toEqual(['Activity summary', 'Total spending', 'Last activity', 'Typical gap']);
      expect(root.querySelector('.insight-list li')!.textContent).toContain('Service');
    });
  });

  describe('history timeline grouped by month', () => {
    type Row = { action: string; date: string; cost?: number | null; provider?: string | null; notes?: string | null };

    // Newest first, as the API returns it.
    const rows: Row[] = [
      { action: 'Serviced', date: '2026-09-20', cost: 850, provider: 'CoolCare', notes: 'Gas checked' },
      { action: 'Cleaned', date: '2026-09-05' },
      { action: 'Repaired', date: '2026-08-30', cost: 1200 },
      { action: 'Purchased', date: '2025-09-10' },
    ];

    async function renderRows(list: Row[]) {
      return render({
        ...detail,
        history: list.map((r, i) => ({
          ...detail.history[0],
          id: `m${i}`,
          cost: null,
          provider: null,
          notes: null,
          ...r,
        })),
      });
    }

    const months = (root: HTMLElement) => Array.from(root.querySelectorAll<HTMLDetailsElement>('details.month'));
    const heading = (d: HTMLDetailsElement) => d.querySelector('summary')!.textContent!.replace(/\s+/g, ' ').trim();
    const actionsIn = (d: HTMLElement) => Array.from(d.querySelectorAll('.entry-action')).map((e) => e.textContent?.trim());

    it('groups by month and year, newest month first, with the activity count', async () => {
      const root = await renderRows(rows);
      expect(months(root).map(heading)).toEqual([
        'September 2026 · 2 activities',
        'August 2026 · 1 activity',
        'September 2025 · 1 activity',
      ]);
    });

    it('expands the latest month and collapses the older ones', async () => {
      const root = await renderRows(rows);
      expect(months(root).map((d) => d.open)).toEqual([true, false, false]);
    });

    it('keeps entries newest first within each month, and overall', async () => {
      const root = await renderRows(rows);
      expect(months(root).map(actionsIn)).toEqual([['Serviced', 'Cleaned'], ['Repaired'], ['Purchased']]);
      expect(actionsIn(root)).toEqual(['Serviced', 'Cleaned', 'Repaired', 'Purchased']); // same order as the API gave
    });

    it('opens and closes a month when its heading is clicked', async () => {
      const root = await renderRows(rows);
      const older = months(root)[1];

      older.querySelector('summary')!.click();
      await TestBed.inject(ApplicationRef).whenStable();
      expect(older.open).toBe(true);

      older.querySelector('summary')!.click();
      await TestBed.inject(ApplicationRef).whenStable();
      expect(older.open).toBe(false);
    });

    it('shows every entry, with date, action, cost, provider, notes and Edit/Delete', async () => {
      const root = await renderRows(rows);
      expect(root.querySelectorAll('.entry').length).toBe(4);

      const first = root.querySelector('.entry')!;
      expect(first.querySelector('.entry-action')!.textContent).toContain('Serviced');
      expect(first.querySelector('.entry-date')!.textContent).toContain('20 Sep 2026');
      expect(first.querySelector('.entry-cost')!.textContent).toContain('₹850');
      expect(first.querySelector('.entry-provider')!.textContent).toContain('CoolCare');
      expect(first.querySelector('.entry-notes')!.textContent).toContain('Gas checked');
      const buttons = Array.from(first.querySelectorAll('.entry-actions button')).map((b) => b.textContent?.trim());
      expect(buttons).toEqual(['Edit', 'Delete']);
    });

    it('omits the cost when there is none', async () => {
      const root = await renderRows(rows);
      const second = root.querySelectorAll('.entry')[1];
      expect(second.querySelector('.entry-cost')).toBeNull();
    });

    it('still opens the edit form for an entry in the expanded month', async () => {
      const root = await renderRows(rows);
      root.querySelector<HTMLButtonElement>('.entry-actions button')!.click();
      await TestBed.inject(ApplicationRef).whenStable();
      expect(root.querySelector('details.month app-history-entry-form')).not.toBeNull();
    });

    it('treats the same month of different years as separate groups', async () => {
      const root = await renderRows([
        { action: 'A', date: '2026-09-01' },
        { action: 'B', date: '2025-09-01' },
      ]);
      expect(months(root).map(heading)).toEqual(['September 2026 · 1 activity', 'September 2025 · 1 activity']);
    });

    it('shows a single expanded group when all entries share a month', async () => {
      const root = await renderRows([
        { action: 'A', date: '2026-09-20' },
        { action: 'B', date: '2026-09-01' },
      ]);
      expect(months(root).length).toBe(1);
      expect(months(root)[0].open).toBe(true);
      expect(heading(months(root)[0])).toBe('September 2026 · 2 activities');
    });

    it('shows no month sections for an empty history, only the existing empty state', async () => {
      const root = await render({ ...detail, history: [] });
      expect(root.querySelector('details.month')).toBeNull();
      expect(root.querySelector('.empty')!.textContent).toContain('No history yet.');
    });

    it('leaves the Insights and "When did I last…?" sections in place above the timeline', async () => {
      const root = await renderRows(rows);
      const firstMonth = root.querySelector('details.month')!;
      for (const selector of ['.insights', '.lookup']) {
        const section = root.querySelector(selector)!;
        expect(section).not.toBeNull();
        expect(section.compareDocumentPosition(firstMonth) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      }
    });
  });

  describe('activity variations', () => {
    type Row = { action: string; date: string };

    // Newest first, as the API returns it.
    const serviceVariants: Row[] = [
      { action: 'Servicing', date: '2025-09-01' },
      { action: 'Service', date: '2025-05-01' },
      { action: 'Serviced', date: '2025-01-01' },
    ];
    const repairVariants: Row[] = [
      { action: 'Repairing', date: '2025-09-01' },
      { action: 'Repairs', date: '2025-05-01' },
      { action: 'Repaired', date: '2025-01-01' },
      { action: 'Repair', date: '2024-09-01' },
    ];

    async function renderRows(rows: Row[]) {
      return render({
        ...detail,
        history: rows.map((r, i) => ({ ...detail.history[0], id: `v${i}`, ...r })),
      });
    }

    const pairs = (root: HTMLElement, selector: string) =>
      Array.from(root.querySelectorAll(selector)).map((li) =>
        Array.from(li.children).map((c) => c.textContent?.trim()),
      );
    const texts = (root: HTMLElement, selector: string) =>
      Array.from(root.querySelectorAll(selector)).map((e) => e.textContent?.trim());

    it('Activity Summary shows one row labeled "Service" for Service, Servicing and Serviced', async () => {
      const root = await renderRows(serviceVariants);
      expect(pairs(root, '.insight-list li')).toEqual([['Service', '3']]);
    });

    it('Typical Gap shows one row labeled "Service"', async () => {
      const root = await renderRows(serviceVariants);
      expect(pairs(root, '.gap-list li')).toEqual([['Service', 'every ~4 months']]);
    });

    it('the label is "Service" whichever variant is newest or first-seen', async () => {
      const orders = [
        serviceVariants, // newest is "Servicing"
        [...serviceVariants].reverse(), // newest is "Serviced"
        [serviceVariants[1], serviceVariants[0], serviceVariants[2]], // newest is "Service"
      ];
      for (const rows of orders) {
        TestBed.resetTestingModule();
        const root = await renderRows(rows);
        expect(pairs(root, '.insight-list li')).toEqual([['Service', '3']]);
        expect(pairs(root, '.gap-list li').map((r) => r[0])).toEqual(['Service']);
        expect(texts(root, '.chip')).toEqual(['Service']);
      }
    });

    it('Repair variants produce one row labeled "Repair" in the summary and Typical Gap', async () => {
      const root = await renderRows(repairVariants);
      expect(pairs(root, '.insight-list li')).toEqual([['Repair', '4']]);
      expect(pairs(root, '.gap-list li').map((r) => r[0])).toEqual(['Repair']);
    });

    it('labels phrases with the base word and keeps different phrases apart', async () => {
      const root = await renderRows([
        { action: 'Bike servicing', date: '2025-09-01' },
        { action: 'Bike service', date: '2025-05-01' },
        { action: 'AC service', date: '2025-03-01' },
        { action: 'Bike serviced', date: '2025-01-01' },
      ]);
      expect(pairs(root, '.insight-list li')).toEqual([['Bike service', '3'], ['AC service', '1']]);
    });

    it('offers one shortcut per normalized activity', async () => {
      const root = await renderRows([...serviceVariants, ...repairVariants]);
      expect(texts(root, '.chip')).toEqual(['Service', 'Repair']);
    });

    it('keeps the original action text in the history timeline', async () => {
      const root = await renderRows(serviceVariants);
      expect(texts(root, '.entry-action')).toEqual(['Servicing', 'Service', 'Serviced']);
    });

    it('still looks up exactly what was typed', async () => {
      const root = await renderRows(serviceVariants);
      const input = root.querySelector<HTMLInputElement>('.lookup input')!;
      input.value = 'servicing';
      input.dispatchEvent(new Event('input'));
      await TestBed.inject(ApplicationRef).whenStable();
      root.querySelector<HTMLFormElement>('.lookup form')!.dispatchEvent(new Event('submit'));

      const req = TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/items/i1/history/last');
      expect(req.request.params.get('action')).toBe('servicing');
      req.flush(detail.history[0]);
    });
  });

  describe('shortcut chips answer locally by normalized activity', () => {
    type Row = { action: string; date: string };

    async function renderRows(rows: Row[]) {
      return render({
        ...detail,
        history: rows.map((r, i) => ({ ...detail.history[0], id: `s${i}`, ...r })),
      });
    }

    const chip = (root: HTMLElement, label: string) =>
      Array.from(root.querySelectorAll<HTMLButtonElement>('.chip')).find((c) => c.textContent?.trim() === label)!;
    const answer = (root: HTMLElement) => root.querySelector('.answer')?.textContent?.replace(/\s+/g, ' ') ?? '';
    const noLookupRequest = () =>
      TestBed.inject(HttpTestingController).expectNone((r) => r.url.includes('/history/last'));

    async function click(root: HTMLElement, label: string) {
      chip(root, label).click();
      await TestBed.inject(ApplicationRef).whenStable();
    }

    async function typeAndSubmit(root: HTMLElement, text: string) {
      const input = root.querySelector<HTMLInputElement>('.lookup input')!;
      input.value = text;
      input.dispatchEvent(new Event('input'));
      await TestBed.inject(ApplicationRef).whenStable();
      root.querySelector<HTMLFormElement>('.lookup form')!.dispatchEvent(new Event('submit'));
    }

    it('Service chip finds the newest entry when history has Service and Servicing', async () => {
      const root = await renderRows([
        { action: 'Servicing', date: '2025-09-01' },
        { action: 'Service', date: '2025-01-01' },
      ]);

      await click(root, 'Service');

      expect(answer(root)).toContain('01 Sep 2025');
      expect(answer(root)).toContain('Servicing'); // the entry's own text is shown
      expect(answer(root)).toContain('Last “Service”');
    });

    it('Service chip matches each of Service, Services, Servicing and Serviced', async () => {
      for (const action of ['Service', 'Services', 'Servicing', 'Serviced']) {
        TestBed.resetTestingModule();
        const root = await renderRows([{ action, date: '2025-09-01' }]);
        await click(root, 'Service');
        expect(answer(root)).toContain(action);
        expect(answer(root)).toContain('01 Sep 2025');
      }
    });

    it('returns the latest normalized match, not an older one', async () => {
      const root = await renderRows([
        { action: 'Serviced', date: '2025-09-01' },
        { action: 'Servicing', date: '2025-05-01' },
        { action: 'Service', date: '2025-01-01' },
      ]);

      await click(root, 'Service');

      expect(answer(root)).toContain('01 Sep 2025');
      expect(answer(root)).not.toContain('01 May 2025');
    });

    it('Service chip does not match Bike service or AC service', async () => {
      const root = await renderRows([
        { action: 'Bike service', date: '2025-09-01' },
        { action: 'AC service', date: '2025-08-01' },
        { action: 'Servicing', date: '2025-01-01' },
      ]);

      await click(root, 'Service');

      expect(answer(root)).toContain('01 Jan 2025');
      expect(answer(root)).not.toContain('Bike service');
      expect(answer(root)).not.toContain('AC service');
    });

    it('the Bike service chip answers only for Bike service', async () => {
      const root = await renderRows([
        { action: 'Service', date: '2025-09-01' },
        { action: 'Bike servicing', date: '2025-02-01' },
      ]);

      await click(root, 'Bike service');

      expect(answer(root)).toContain('01 Feb 2025');
    });

    it('Repair chip matches Repair, Repairs, Repairing and Repaired', async () => {
      for (const action of ['Repair', 'Repairs', 'Repairing', 'Repaired']) {
        TestBed.resetTestingModule();
        const root = await renderRows([{ action, date: '2025-09-01' }]);
        await click(root, 'Repair');
        expect(answer(root)).toContain(action);
      }
    });

    it('a chip click does not call the lookup API', async () => {
      const root = await renderRows([
        { action: 'Servicing', date: '2025-09-01' },
        { action: 'Service', date: '2025-01-01' },
      ]);

      await click(root, 'Service');

      noLookupRequest();
      expect(root.querySelector('.answer')).not.toBeNull();
    });

    it('typed "Service" still calls the API with Service', async () => {
      const root = await renderRows([{ action: 'Servicing', date: '2025-09-01' }]);
      await typeAndSubmit(root, 'Service');

      const req = TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/items/i1/history/last');
      expect(req.request.params.get('action')).toBe('Service');
      req.flush('none', { status: 404, statusText: 'Not Found' });
    });

    it('typed "Servicing" still calls the API with Servicing', async () => {
      const root = await renderRows([{ action: 'Service', date: '2025-09-01' }]);
      await typeAndSubmit(root, 'Servicing');

      const req = TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/items/i1/history/last');
      expect(req.request.params.get('action')).toBe('Servicing');
      req.flush(detail.history[0]);
    });

    it('leaves the history timeline showing the original text', async () => {
      const root = await renderRows([
        { action: 'Servicing', date: '2025-09-01' },
        { action: 'Service', date: '2025-01-01' },
      ]);
      await click(root, 'Service');
      expect(Array.from(root.querySelectorAll('.entry-action')).map((e) => e.textContent?.trim())).toEqual([
        'Servicing',
        'Service',
      ]);
    });
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
      expect(chips).toEqual(['Service', 'Purchased']);
    });

    it('answers with the date, cost and provider when a shortcut is clicked', async () => {
      const { root, http } = await renderWithHttp();

      root.querySelector<HTMLButtonElement>('.chip')!.click();
      http.expectNone((r) => r.url.includes('/history/last')); // answered from the loaded history
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

      it('deduplicates case-insensitively, keeping the first occurrence casing (known words use the base word)', async () => {
        const root = await renderActions(['service', 'Service', 'SERVICE', 'Repair']);
        expect(chips(root)).toEqual(['Service', 'Repair']);
      });

      it('filters by the typed text (case-insensitive, contains), deduplicated', async () => {
        const root = await renderActions(actions);
        await type(root, 'SER');
        expect(chips(root)).toEqual(['Service', 'AC service', 'Bike service']);
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
