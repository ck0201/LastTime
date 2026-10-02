import { CurrencyPipe, DatePipe } from '@angular/common';
import { Component, ElementRef, Injector, afterNextRender, effect, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, debounceTime, map, of, startWith, switchMap } from 'rxjs';
import { Auth } from '../../core/auth';
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
  host: { '(window:resize)': 'measure()' },
})
export class ItemList {
  private readonly api = inject(ItemsApi);
  protected readonly auth = inject(Auth);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

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

  /** Items whose description is shown in full ("Read less"), and items whose description is cut off when collapsed. */
  protected readonly expanded = signal<ReadonlySet<string>>(new Set());
  protected readonly overflowing = signal<ReadonlySet<string>>(new Set());

  constructor() {
    // Whether a description is cut off depends on layout, so look once the list has rendered.
    effect(() => {
      this.state();
      afterNextRender(() => this.measure(), { injector: this.injector });
    });
  }

  protected onSearch(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value);
  }

  protected toggleDescription(id: string): void {
    this.expanded.update((ids) => {
      const next = new Set(ids);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  /** Marks collapsed descriptions that do not fit their line limit. Expanded ones keep their previous result. */
  protected measure(): void {
    const current = this.overflowing();
    const next = new Set(current);
    for (const el of this.host.nativeElement.querySelectorAll<HTMLElement>('.card-sub-clamped')) {
      const id = el.dataset['id']!;
      if (el.scrollHeight > el.clientHeight + 1) next.add(id);
      else next.delete(id);
    }
    if (next.size !== current.size || [...next].some((id) => !current.has(id))) this.overflowing.set(next);
  }
}
