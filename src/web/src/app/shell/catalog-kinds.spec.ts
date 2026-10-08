import { labelledAttributes, siteRole } from './catalog-kinds';

describe('labelledAttributes', () => {
  const fields = [
    { key: 'backupHours', type: 'number', title: 'Reservkraft (timmar)' },
    { key: 'aliases', type: 'array', title: 'Andra namn' },
    { key: 'owner', type: 'string', title: null },
  ];

  it('names attributes from the schema, in its order, then the rest by key', () => {
    const rows = labelledAttributes(fields, {
      zeta: 1,
      aliases: ['Norra', 'N1'],
      backupHours: 4,
      alpha: 'x',
    });
    expect(rows).toEqual([
      ['Reservkraft (timmar)', '4'],
      ['Andra namn', 'Norra, N1'],
      ['alpha', 'x'],
      ['zeta', '1'],
    ]);
  });

  it('uses the key when the schema field has no title', () => {
    expect(labelledAttributes(fields, { owner: 'Nät' })).toEqual([['owner', 'Nät']]);
  });

  it('lists free attributes by key when the type has no schema', () => {
    expect(labelledAttributes([], { b: true, a: 2 })).toEqual([
      ['a', '2'],
      ['b', 'true'],
    ]);
  });
});

describe('siteRole', () => {
  it('picks the most prominent role of the site type', () => {
    const kinds = [{ key: 'nod', name: 'Nod', roles: ['access', 'aggregation'] }];
    expect(siteRole(kinds, 'nod')).toBe('aggregation');
    expect(siteRole(kinds, 'okänd')).toBeNull();
  });
});
