/**
 * Common variations of an activity word, mapped to one base word. Deliberately tiny: add a line here when real
 * usage shows another variation worth grouping. Words not listed are only lowercased.
 */
const BASE_WORD: Record<string, string> = {
  services: 'service',
  servicing: 'service',
  serviced: 'service',
  repairs: 'repair',
  repairing: 'repair',
  repaired: 'repair',
};

/**
 * Grouping key for insights: "Bike Servicing" and "bike service" both give "bike service", while "AC service"
 * stays different. It is only a key; the entry's own action text is never changed.
 */
export function actionKey(action: string): string {
  return action
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .map((word) => BASE_WORD[word] ?? word)
    .join(' ');
}

const BASE_WORDS = new Set(Object.values(BASE_WORD));

/**
 * Display label for a group of actions: known variations show as their base word ("Servicing" and "serviced" both
 * give "Service", "Bike servicing" gives "Bike service"), so the label never depends on which entry came first or last.
 * Text without a known word is returned as typed.
 */
export function actionLabel(action: string): string {
  const words = action.trim().split(/\s+/);
  const baseOf = (word: string) => {
    const lower = word.toLowerCase();
    return BASE_WORD[lower] ?? (BASE_WORDS.has(lower) ? lower : undefined);
  };
  if (!words.some(baseOf)) return action.trim();

  const label = words.map((word) => baseOf(word) ?? word).join(' ');
  return label.charAt(0).toUpperCase() + label.slice(1);
}
