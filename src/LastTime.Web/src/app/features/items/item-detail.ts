import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { costFormat, relativeDate } from '../../core/format';
import { ItemsApi } from '../../core/items-api';
import { HistoryEntry, HistoryEntryRequest, ItemDetail } from '../../core/models';

type Lookup =
  | { status: 'idle' }
  | { status: 'loading'; term: string }
  | { status: 'found'; term: string; entry: HistoryEntry }
  | { status: 'none'; term: string }
  | { status: 'error'; term: string };
import { HistoryEntryForm } from '../history/history-entry-form';

const MAX_SHORTCUTS = 5;

@Component({
  selector: 'app-item-detail',
  imports: [RouterLink, DatePipe, CurrencyPipe, HistoryEntryForm],
  templateUrl: './item-detail.html',
})
export class ItemDetailPage implements OnInit {
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
  protected readonly actionError = signal<string | null>(null);

  protected readonly costFormat = costFormat;
  protected readonly relativeDate = relativeDate;

  /** "When did I last…?" lookup. */
  protected readonly lookupTerm = signal('');
  protected readonly lookup = signal<Lookup>({ status: 'idle' });

  /** Distinct actions already used on this item (case-insensitive), most recent first — offered as shortcuts. */
  protected readonly actionShortcuts = computed(() => {
    const seen = new Map<string, string>();
    for (const e of this.item()?.history ?? []) {
      const key = e.action.toLowerCase();
      if (!seen.has(key)) seen.set(key, e.action);
    }
    return [...seen.values()];
  });

  /** Insights from the history already loaded; null when there is none. History is newest first, so [0] is the last activity. */
  protected readonly insights = computed(() => {
    const history = this.item()?.history ?? [];
    if (history.length === 0) return null;

    // Group case-insensitively, keeping the first-seen casing; Map keeps first-seen order, and the stable sort keeps it for ties.
    const groups = new Map<string, { action: string; count: number }>();
    for (const e of history) {
      const key = e.action.toLowerCase();
      const group = groups.get(key);
      if (group) group.count++;
      else groups.set(key, { action: e.action, count: 1 });
    }
    const actions = [...groups.values()].sort((a, b) => b.count - a.count);

    // Null means "not recorded"; 0 is a real cost. Sum in paise so decimals don't drift.
    const costs = history.filter((e) => e.cost !== null).map((e) => Math.round(e.cost! * 100));
    const total = costs.length > 0 ? costs.reduce((sum, c) => sum + c, 0) / 100 : null;

    return { actions, total, last: history[0] };
  });

  /** The shortcuts narrowed to what is typed (case-insensitive "contains"), capped at 5; `more` adds a non-clickable "...". */
  protected readonly shortcutView = computed(() => {
    const typed = this.lookupTerm().trim().toLowerCase();
    const matches = this.actionShortcuts().filter((a) => a.toLowerCase().includes(typed));
    return { shown: matches.slice(0, MAX_SHORTCUTS), more: matches.length > MAX_SHORTCUTS };
  });

  ngOnInit(): void {
    this.load();
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
