import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, debounceTime, map, of, startWith, switchMap } from 'rxjs';
import { costFormat, relativeDate } from '../../core/format';
import { ItemsApi } from '../../core/items-api';
import { ItemSummary } from '../../core/models';

interface ListState {
  status: 'loading' | 'ok' | 'error';
  items: ItemSummary[];
}

@Component({
  selector: 'app-item-list',
  imports: [RouterLink, DatePipe, CurrencyPipe],
  templateUrl: './item-list.html',
})
export class ItemList {
  private readonly api = inject(ItemsApi);

  protected readonly costFormat = costFormat;
  protected readonly relativeDate = relativeDate;

  protected readonly search = signal('');

  protected readonly state = toSignal(
    toObservable(this.search).pipe(
      debounceTime(250),
      switchMap((term) =>
        this.api.list(term).pipe(
          map((items): ListState => ({ status: 'ok', items })),
          catchError(() => of<ListState>({ status: 'error', items: [] })),
          startWith<ListState>({ status: 'loading', items: [] }),
        ),
      ),
    ),
    { initialValue: { status: 'loading', items: [] } as ListState },
  );

  protected onSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }
}
