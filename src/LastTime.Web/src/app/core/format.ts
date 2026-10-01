/** Whole rupees show without decimals ("₹1,200"); fractional amounts show two ("₹850.50"). */
export function costFormat(cost: number): string {
  return Number.isInteger(cost) ? '1.0-0' : '1.2-2';
}

/** "today", "yesterday", "12 days ago", "5 months ago", "3 years ago" for a "YYYY-MM-DD" date. */
export function relativeDate(dateIso: string, now: Date = new Date()): string {
  const [y, m, d] = dateIso.split('-').map(Number);
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const days = Math.round((startOfToday.getTime() - new Date(y, m - 1, d).getTime()) / 86_400_000);

  if (days < 0) return 'upcoming';
  if (days === 0) return 'today';
  if (days === 1) return 'yesterday';
  if (days < 60) return `${days} days ago`;
  if (days < 730) return `${Math.floor(days / 30.44)} months ago`;
  return `${Math.floor(days / 365.25)} years ago`;
}

/** Whole calendar days from one "YYYY-MM-DD" date to a later one (UTC math, so daylight saving cannot skew it). */
export function daysBetween(fromIso: string, toIso: string): number {
  const utcDay = (iso: string) => {
    const [y, m, d] = iso.split('-').map(Number);
    return Date.UTC(y, m - 1, d) / 86_400_000;
  };
  return utcDay(toIso) - utcDay(fromIso);
}

/** A typical gap in days as short text: "daily", "every ~5 days", "every ~3 weeks", "every ~4 months", "every ~1 year". */
export function gapLabel(days: number): string {
  if (days < 1.5) return 'daily';
  const plural = (n: number, unit: string) => `every ~${n} ${unit}${n === 1 ? '' : 's'}`;
  if (days < 14) return plural(Math.round(days), 'day');
  if (days < 45) return plural(Math.round(days / 7), 'week');
  const months = Math.round(days / 30.44);
  if (months === 12) return plural(1, 'year');
  if (months < 24) return plural(months, 'month');
  return plural(Math.round(days / 365.25), 'year');
}

/** Today's local calendar date as "YYYY-MM-DD" (what <input type="date"> and the API expect). */
export function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
