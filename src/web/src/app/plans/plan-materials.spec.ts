import { describe, expect, it } from 'vitest';
import { materialsCsv, money, PlanMaterials } from './plan-materials';

const materials: PlanMaterials = {
  currency: 'SEK',
  lines: [
    {
      kind: 'cable',
      key: 'fiber-24',
      name: 'Fiberkabel 24',
      quantity: 2406.5,
      unit: 'm',
      unitCost: 22,
      cost: 52943,
    },
    {
      kind: 'equipment',
      key: 'acme-odf-96',
      name: 'Acme ODF-96; front',
      quantity: 2,
      unit: 'st',
      unitCost: 6500,
      cost: 13000,
    },
    {
      kind: 'construction',
      key: 'trench',
      name: 'Ny trasé, grävd',
      quantity: 120,
      unit: 'm',
      unitCost: null,
      cost: null,
    },
  ],
  total: 65943,
  missing: ['trench'],
  elapsedMs: 3,
};

describe('a plan’s materials (#291)', () => {
  it('reads sums of money in kronor, or with the currency code', () => {
    expect(money(412000, 'SEK').replace(/\s/g, ' ')).toBe('412 000 kr');
    expect(money(1500.4, 'EUR').replace(/\s/g, ' ')).toBe('1 500 EUR');
  });

  it('exports CSV for Excel: a byte order mark, semicolons, decimal commas, quoted cells and a sum', () => {
    const csv = materialsCsv(materials);
    expect(csv.startsWith('﻿')).toBe(true);
    const rows = csv.slice(1).trimEnd().split('\r\n');
    expect(rows[0]).toBe('Slag;Nyckel;Namn;Mängd;Enhet;Pris per enhet (SEK);Kostnad (SEK)');
    expect(rows[1]).toBe('Kabel;fiber-24;Fiberkabel 24;2406,5;m;22;52943');
    expect(rows[2]).toBe('Utrustning;acme-odf-96;"Acme ODF-96; front";2;st;6500;13000');
    expect(rows[3]).toBe('Trasé;trench;Ny trasé, grävd;120;m;;');
    expect(rows[4]).toBe(';;Summa;;;;65943');
  });
});
