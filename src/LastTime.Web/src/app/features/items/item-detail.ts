import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, inject, input, OnInit, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { costFormat } from '../../core/format';
import { ItemsApi } from '../../core/items-api';
import { HistoryEntryRequest, ItemDetail } from '../../core/models';
import { HistoryEntryForm } from '../history/history-entry-form';

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

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
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
