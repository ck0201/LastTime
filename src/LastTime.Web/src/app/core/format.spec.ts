import { costFormat, daysBetween, gapLabel, relativeDate } from './format';

describe('relativeDate', () => {
  const now = new Date(2026, 8, 30, 15, 30); // 30 Sep 2026, mid-afternoon

  it.each([
    ['2026-09-30', 'today'],
    ['2026-09-29', 'yesterday'],
    ['2026-09-18', '12 days ago'],
    ['2026-08-02', '59 days ago'],
    ['2026-07-30', '2 months ago'],
    ['2026-02-03', '7 months ago'],
    ['2024-03-14', '2 years ago'],
    ['2026-10-05', 'upcoming'],
  ])('%s → %s', (date, expected) => {
    expect(relativeDate(date, now)).toBe(expected);
  });

  it('is not thrown off by the time of day', () => {
    expect(relativeDate('2026-09-29', new Date(2026, 8, 30, 0, 5))).toBe('yesterday');
    expect(relativeDate('2026-09-29', new Date(2026, 8, 30, 23, 55))).toBe('yesterday');
  });
});

describe('costFormat', () => {
  it('drops decimals for whole rupees only', () => {
    expect(costFormat(1200)).toBe('1.0-0');
    expect(costFormat(850.5)).toBe('1.2-2');
  });
});

describe('daysBetween', () => {
  it('counts whole calendar days', () => {
    expect(daysBetween('2025-01-01', '2025-01-01')).toBe(0);
    expect(daysBetween('2025-01-01', '2025-05-01')).toBe(120);
    expect(daysBetween('2024-02-28', '2024-03-01')).toBe(2); // leap year
  });

  it('is not skewed by daylight saving changes', () => {
    expect(daysBetween('2026-03-01', '2026-04-01')).toBe(31);
    expect(daysBetween('2026-10-01', '2026-11-01')).toBe(31);
  });
});

describe('gapLabel', () => {
  it.each([
    [0, 'daily'],
    [0.4, 'daily'],
    [1.4, 'daily'],
    [1.5, 'every ~2 days'],
    [5, 'every ~5 days'],
    [13.4, 'every ~13 days'],
    [14, 'every ~2 weeks'],
    [21, 'every ~3 weeks'],
    [44, 'every ~6 weeks'],
    [45, 'every ~1 month'],
    [60, 'every ~2 months'],
    [121.5, 'every ~4 months'],
    [365, 'every ~1 year'],
    [400, 'every ~13 months'],
    [700, 'every ~23 months'],
    [730, 'every ~2 years'],
    [1100, 'every ~3 years'],
  ])('%s days → %s', (days, expected) => {
    expect(gapLabel(days)).toBe(expected);
  });

  it('never says "~0 days"', () => {
    for (const days of [0, 0.1, 0.5, 1, 1.49]) expect(gapLabel(days)).not.toContain('0 day');
  });
});
