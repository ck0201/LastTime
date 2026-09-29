export interface Item {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
}

export interface HistoryEntry {
  id: string;
  itemId: string;
  /** Calendar date, "YYYY-MM-DD". */
  date: string;
  action: string;
  cost: number | null;
  provider: string | null;
  notes: string | null;
  createdAt: string;
}

export interface ItemSummary extends Item {
  /** The most recent history entry, or null if the item has none. */
  lastEntry: HistoryEntry | null;
}

export interface ItemDetail extends Item {
  /** Newest first. */
  history: HistoryEntry[];
}

export interface ItemRequest {
  name: string;
  description: string | null;
}

export interface HistoryEntryRequest {
  date: string;
  action: string;
  cost: number | null;
  provider: string | null;
  notes: string | null;
}
