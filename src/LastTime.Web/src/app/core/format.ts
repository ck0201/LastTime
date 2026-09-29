/** Whole rupees show without decimals ("₹1,200"); fractional amounts show two ("₹850.50"). */
export function costFormat(cost: number): string {
  return Number.isInteger(cost) ? '1.0-0' : '1.2-2';
}

/** Today's local calendar date as "YYYY-MM-DD" (what <input type="date"> and the API expect). */
export function todayIso(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
