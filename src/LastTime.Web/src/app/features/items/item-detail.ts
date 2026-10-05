import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, input, OnDestroy, OnInit, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { actionKey, actionLabel } from '../../core/action-key';
import { costFormat, daysBetween, gapLabel, relativeDate, todayIso } from '../../core/format';
import { ItemsApi } from '../../core/items-api';
import { HistoryEntry, HistoryEntryRequest, ItemDetail } from '../../core/models';

type Lookup =
  | { status: 'idle' }
  | { status: 'loading'; term: string }
  | { status: 'found'; term: string; entry: HistoryEntry }
  | { status: 'none'; term: string }
  | { status: 'error'; term: string };
import { HistoryEntryForm } from '../history/history-entry-form';

/** A "When did I last…?" chip: the label shown, and the actionKey its entries are matched by. */
interface Shortcut {
  label: string;
  key: string;
}

const MAX_SHORTCUTS = 5;
const MIN_DATES_FOR_GAP = 3;
const MAX_GAPS = 3;

@Component({
  selector: 'app-item-detail',
  imports: [RouterLink, DatePipe, CurrencyPipe, HistoryEntryForm],
  templateUrl: './item-detail.html',
})
export class ItemDetailPage implements OnInit, OnDestroy {
  private readonly api = inject(ItemsApi);
  private readonly router = inject(Router);

  /** Bound from the route. */
  readonly id = input.required<string>();

  protected readonly item = signal<ItemDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly loadError = signal(false);

  /** Which inline form is open: adding a new entry, or editing the entry with this id. */
  protected readonly adding = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  /** Inline two-step delete confirmation (avoids blocking browser dialogs). */
  protected readonly confirmDeleteEntryId = signal<string | null>(null);
  protected readonly confirmDeleteItem = signal(false);

  /** The ⋮ menu in the item header (Edit / Delete). Closing it also abandons a half-finished delete confirmation. */
  protected readonly menuOpen = signal(false);

  protected toggleMenu(): void {
    if (this.menuOpen()) this.closeMenu();
    else this.menuOpen.set(true);
  }

  protected closeMenu(): void {
    this.menuOpen.set(false);
    this.confirmDeleteItem.set(false);
  }
  protected readonly actionError = signal<string | null>(null);

  protected readonly costFormat = costFormat;
  protected readonly relativeDate = relativeDate;

  /** "When did I last…?" lookup. */
  protected readonly lookupTerm = signal('');
  protected readonly lookup = signal<Lookup>({ status: 'idle' });

  /** Distinct activities already used on this item (grouped like the insights), most recent first — offered as shortcuts. */
  protected readonly actionShortcuts = computed<Shortcut[]>(() => {
    const seen = new Map<string, Shortcut>();
    for (const e of this.item()?.history ?? []) {
      const key = actionKey(e.action);
      if (!seen.has(key)) seen.set(key, { label: actionLabel(e.action), key });
    }
    return [...seen.values()];
  });

  /** The history grouped by month ("YYYY-MM"), in the order received (newest first), so the first group is the latest month. */
  protected readonly monthGroups = computed(() => {
    const groups = new Map<string, { key: string; date: string; entries: HistoryEntry[] }>();
    for (const e of this.item()?.history ?? []) {
      const key = e.date.slice(0, 7);
      const group = groups.get(key);
      if (group) group.entries.push(e);
      else groups.set(key, { key, date: e.date, entries: [e] });
    }
    return [...groups.values()];
  });

  /** Insights from the history already loaded; null when there is none. History is newest first, so [0] is the last activity. */
  protected readonly insights = computed(() => {
    const history = this.item()?.history ?? [];
    if (history.length === 0) return null;

    // Group by actionKey and label with actionLabel; Map keeps first-seen order, and the stable sort keeps it for ties.
    const groups = new Map<string, { action: string; count: number }>();
    for (const e of history) {
      const key = actionKey(e.action);
      const group = groups.get(key);
      if (group) group.count++;
      else groups.set(key, { action: actionLabel(e.action), count: 1 });
    }
    const actions = [...groups.values()].sort((a, b) => b.count - a.count);

    // Null means "not recorded"; 0 is a real cost. Sum in paise so decimals don't drift.
    const costs = history.filter((e) => e.cost !== null).map((e) => Math.round(e.cost! * 100));
    const total = costs.length > 0 ? costs.reduce((sum, c) => sum + c, 0) / 100 : null;

    return { actions, total, last: history[0] };
  });

  /**
   * "Typical gap": the mean gap between the distinct past dates of an action, for actions with at least 3 such dates.
   * Same grouping and order as the activity summary; at most 3 shown. Descriptive only, not a forecast.
   */
  protected readonly typicalGaps = computed(() => {
    const ins = this.insights();
    if (!ins) return [];

    const today = todayIso();
    const datesByAction = new Map<string, Set<string>>();
    for (const e of this.item()!.history) {
      if (e.date > today) continue; // "YYYY-MM-DD" strings compare chronologically
      const key = actionKey(e.action);
      if (!datesByAction.has(key)) datesByAction.set(key, new Set());
      datesByAction.get(key)!.add(e.date);
    }

    const gaps: { action: string; label: string }[] = [];
    for (const { action } of ins.actions) {
      const dates = [...(datesByAction.get(actionKey(action)) ?? [])].sort();
      if (dates.length < MIN_DATES_FOR_GAP) continue;
      const meanDays = daysBetween(dates[0], dates[dates.length - 1]) / (dates.length - 1);
      gaps.push({ action, label: gapLabel(meanDays) });
      if (gaps.length === MAX_GAPS) break;
    }
    return gaps;
  });

  /** The shortcuts narrowed to what is typed (case-insensitive "contains"), capped at 5; `more` adds a non-clickable "...". */
  protected readonly shortcutView = computed(() => {
    const typed = this.lookupTerm().trim().toLowerCase();
    const matches = this.actionShortcuts().filter((a) => a.label.toLowerCase().includes(typed));
    return { shown: matches.slice(0, MAX_SHORTCUTS), more: matches.length > MAX_SHORTCUTS };
  });

  /** Timestamp displayed in printable / PDF report. */
  protected readonly exportDate = signal(new Date());

  private originalTitleBeforePrint: string | null = null;

  private readonly beforePrintListener = (): void => {
    this.exportDate.set(new Date());
    const current = this.item();
    if (current && this.originalTitleBeforePrint === null) {
      this.originalTitleBeforePrint = document.title;
      const itemName = current.name?.trim() || 'Item';
      document.title = `${itemName} - History Report - Last Time`;
    }
  };

  private readonly afterPrintListener = (): void => {
    if (this.originalTitleBeforePrint !== null) {
      document.title = this.originalTitleBeforePrint;
      this.originalTitleBeforePrint = null;
    }
  };

  protected exportPdf(): void {
    if (!this.item()) return;
    this.closeMenu();
    this.beforePrintListener();
    if (typeof window !== 'undefined' && typeof window.print === 'function') {
      window.print();
    }
    setTimeout(this.afterPrintListener, 2000);
  }

  ngOnInit(): void {
    this.load();
    if (typeof window !== 'undefined') {
      window.addEventListener('beforeprint', this.beforePrintListener);
      window.addEventListener('afterprint', this.afterPrintListener);
    }
  }

  ngOnDestroy(): void {
    if (typeof window !== 'undefined') {
      window.removeEventListener('beforeprint', this.beforePrintListener);
      window.removeEventListener('afterprint', this.afterPrintListener);
    }
  }

  protected onLookupInput(event: Event): void {
    this.lookupTerm.set((event.target as HTMLInputElement).value);
  }

  protected onLookupSubmit(event: Event): void {
    event.preventDefault();
    this.runLookup(this.lookupTerm());
  }

  protected runLookup(action: string): void {
    const term = action.trim();
    if (!term) return;

    this.lookupTerm.set(term);
    this.lookup.set({ status: 'loading', term });
    this.api.last(this.id(), term).subscribe({
      next: (entry) => this.lookup.set(entry ? { status: 'found', term, entry } : { status: 'none', term }),
      error: () => this.lookup.set({ status: 'error', term }),
    });
  }

  /**
   * A shortcut is an exact normalized activity, and the page already holds the whole history (newest first),
   * so it is answered locally: the first entry with the same actionKey. Typed lookups still go to the API.
   */
  protected runShortcut(shortcut: Shortcut): void {
    const term = shortcut.label;
    const entry = this.item()?.history.find((e) => actionKey(e.action) === shortcut.key);
    this.lookupTerm.set(term);
    this.lookup.set(entry ? { status: 'found', term, entry } : { status: 'none', term });
  }

  private load(): void {
    this.lookup.set({ status: 'idle' }); // history may have changed, so an old answer could be stale
    this.api.get(this.id()).subscribe({
      next: (item) => {
        this.item.set(item);
        this.loading.set(false);
      },
      error: () => {
        this.loadError.set(true);
        this.loading.set(false);
      },
    });
  }

  protected startAdd(): void {
    this.closeForms();
    this.adding.set(true);
  }

  protected startEdit(entryId: string): void {
    this.closeForms();
    this.editingId.set(entryId);
  }

  protected closeForms(): void {
    this.adding.set(false);
    this.editingId.set(null);
    this.formError.set(null);
  }

  protected saveEntry(request: HistoryEntryRequest): void {
    const editingId = this.editingId();
    const save$: Observable<unknown> = editingId
      ? this.api.updateEntry(this.id(), editingId, request)
      : this.api.addEntry(this.id(), request);

    this.saving.set(true);
    this.formError.set(null);
    save$.subscribe({
      next: () => {
        this.saving.set(false);
        this.closeForms();
        this.load();
      },
      error: () => {
        this.saving.set(false);
        this.formError.set("Couldn't save the entry. Please try again.");
      },
    });
  }

  protected deleteEntry(entryId: string): void {
    this.actionError.set(null);
    this.api.deleteEntry(this.id(), entryId).subscribe({
      next: () => {
        this.confirmDeleteEntryId.set(null);
        this.load();
      },
      error: () => this.actionError.set("Couldn't delete the entry."),
    });
  }

  protected deleteItem(): void {
    this.actionError.set(null);
    this.api.delete(this.id()).subscribe({
      next: () => this.router.navigateByUrl('/'),
      error: () => this.actionError.set("Couldn't delete the item."),
    });
  }
}
