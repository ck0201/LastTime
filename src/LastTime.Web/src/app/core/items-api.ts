import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of, throwError } from 'rxjs';
import { HistoryEntry, HistoryEntryRequest, Item, ItemDetail, ItemRequest, ItemSummary } from './models';

@Injectable({ providedIn: 'root' })
export class ItemsApi {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/items';

  list(search = ''): Observable<ItemSummary[]> {
    const term = search.trim();
    const params = term ? new HttpParams().set('search', term) : undefined;
    return this.http.get<ItemSummary[]>(this.base, { params });
  }

  /** "When did I last…?" — the latest entry whose action contains `action`, or null if there is none. */
  last(itemId: string, action: string): Observable<HistoryEntry | null> {
    return this.http
      .get<HistoryEntry>(`${this.base}/${itemId}/history/last`, { params: new HttpParams().set('action', action.trim()) })
      .pipe(catchError((e: HttpErrorResponse) => (e.status === 404 ? of(null) : throwError(() => e))));
  }

  get(id: string): Observable<ItemDetail> {
    return this.http.get<ItemDetail>(`${this.base}/${id}`);
  }

  create(request: ItemRequest): Observable<Item> {
    return this.http.post<Item>(this.base, request);
  }

  update(id: string, request: ItemRequest): Observable<Item> {
    return this.http.put<Item>(`${this.base}/${id}`, request);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  addEntry(itemId: string, request: HistoryEntryRequest): Observable<HistoryEntry> {
    return this.http.post<HistoryEntry>(`${this.base}/${itemId}/history`, request);
  }

  updateEntry(itemId: string, entryId: string, request: HistoryEntryRequest): Observable<HistoryEntry> {
    return this.http.put<HistoryEntry>(`${this.base}/${itemId}/history/${entryId}`, request);
  }

  deleteEntry(itemId: string, entryId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${itemId}/history/${entryId}`);
  }
}
