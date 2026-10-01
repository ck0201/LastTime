import { actionKey, actionLabel } from './action-key';

describe('actionKey', () => {
  it('treats service, services, servicing and serviced as the same activity', () => {
    const keys = ['Service', 'Services', 'Servicing', 'Serviced'].map(actionKey);
    expect(new Set(keys)).toEqual(new Set(['service']));
  });

  it('treats repair, repairs, repairing and repaired as the same activity', () => {
    const keys = ['Repair', 'Repairs', 'Repairing', 'Repaired'].map(actionKey);
    expect(new Set(keys)).toEqual(new Set(['repair']));
  });

  it('ignores case and surrounding or repeated whitespace', () => {
    expect(actionKey('SERVICING')).toBe(actionKey('service'));
    expect(actionKey('  Bike   Service ')).toBe('bike service');
  });

  it('groups phrase variants', () => {
    const keys = ['Bike service', 'Bike services', 'Bike servicing', 'Bike serviced'].map(actionKey);
    expect(new Set(keys)).toEqual(new Set(['bike service']));
    expect(actionKey('AC service')).toBe(actionKey('AC servicing'));
  });

  it('keeps different phrases different', () => {
    expect(actionKey('Bike service')).not.toBe(actionKey('AC service'));
    expect(actionKey('Service')).not.toBe(actionKey('AC service'));
    expect(actionKey('Service bike')).not.toBe(actionKey('Bike service'));
  });

  it('leaves unknown words lowercased and distinguishable', () => {
    expect(actionKey('Purchased')).toBe('purchased');
    expect(actionKey('Purchase')).toBe('purchase');
    expect(actionKey('Cleaning')).not.toBe(actionKey('Cleaned'));
  });

  it('does not alter words that merely contain a listed word', () => {
    expect(actionKey('Servicemen')).toBe('servicemen');
    expect(actionKey('Prepaired')).toBe('prepaired');
  });
});

describe('actionLabel', () => {
  it('shows service and repair variations as their base word, whatever the casing', () => {
    for (const a of ['Service', 'service', 'SERVICE', 'Services', 'Servicing', 'Serviced']) {
      expect(actionLabel(a)).toBe('Service');
    }
    for (const a of ['Repair', 'repairs', 'REPAIRING', 'Repaired']) {
      expect(actionLabel(a)).toBe('Repair');
    }
  });

  it('keeps the other words of a phrase as typed', () => {
    expect(actionLabel('Bike servicing')).toBe('Bike service');
    expect(actionLabel('AC serviced')).toBe('AC service');
    expect(actionLabel('bike service')).toBe('Bike service');
  });

  it('returns text without a known word exactly as typed', () => {
    expect(actionLabel('purchased')).toBe('purchased');
    expect(actionLabel('  Oil Change ')).toBe('Oil Change');
    expect(actionLabel('Servicemen')).toBe('Servicemen');
  });

  it('gives the same label for everything that shares an actionKey', () => {
    const variants = ['Bike service', 'bike servicing', 'Bike Serviced'];
    expect(new Set(variants.map(actionKey)).size).toBe(1);
    expect(new Set(variants.map(actionLabel)).size).toBe(1);
  });
});
