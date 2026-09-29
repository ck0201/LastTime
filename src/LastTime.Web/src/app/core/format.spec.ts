import { costFormat, relativeDate } from './format';

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
