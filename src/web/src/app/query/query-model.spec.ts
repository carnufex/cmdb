import { emptyEquipment, examples, examplesFor, QueryFields, toRequest } from './query-model';

const fields: QueryFields = {
  siteTypes: ['radio'],
  lifecycles: ['planned', 'in_service'],
  serviceTypes: ['ethernet'],
  categories: [
    {
      key: 'radio',
      attributes: [
        { key: 'bandMHz', type: 'number', values: [700, 3500] },
        { key: 'serialNumber', type: 'string', values: null },
      ],
    },
  ],
  types: [],
};

describe('toRequest', () => {
  it('sends numbers for numeric attributes and strings for text', () => {
    const request = toRequest(
      {
        siteTypes: ['radio'],
        lifecycles: [],
        serviceTypes: [],
        equipment: [
          { ...emptyEquipment(), category: 'radio', key: 'bandMHz', op: 'gte', value: '3500' },
          {
            ...emptyEquipment(),
            category: 'radio',
            key: 'serialNumber',
            op: 'prefix',
            value: 'SN1',
          },
        ],
      },
      fields,
    );

    expect(request.siteTypes).toEqual(['radio']);
    expect(request.lifecycles).toBeUndefined();
    expect(request.equipment[0].attribute).toEqual({ key: 'bandMHz', op: 'gte', value: 3500 });
    expect(request.equipment[1].attribute).toEqual({
      key: 'serialNumber',
      op: 'prefix',
      value: 'SN1',
    });
  });

  it('drops blank conditions, sends no value for exists and only counts above one', () => {
    const request = toRequest(
      {
        siteTypes: [],
        lifecycles: [],
        serviceTypes: [],
        equipment: [
          emptyEquipment(),
          { ...emptyEquipment(), category: 'radio', key: 'bandMHz', op: 'exists', value: 'x' },
          { ...emptyEquipment(), category: 'antenna', minCount: 3 },
        ],
      },
      fields,
    );

    expect(request.equipment.length).toBe(2);
    expect(request.equipment[0].attribute).toEqual({
      key: 'bandMHz',
      op: 'exists',
      value: undefined,
    });
    expect(request.equipment[1]).toEqual({
      category: 'antenna',
      typeKey: undefined,
      minCount: 3,
      attribute: undefined,
    });
  });

  it('turns every example into a request with at least one condition', () => {
    for (const example of examples) {
      const r = toRequest(example.draft, fields);
      const conditions =
        (r.siteTypes?.length ?? 0) +
        (r.lifecycles?.length ?? 0) +
        (r.serviceTypes?.length ?? 0) +
        r.equipment.length;
      expect(conditions, example.label).toBeGreaterThan(0);
    }
  });
});

describe('examplesFor', () => {
  it('offers only examples whose site types, categories and attributes the catalog has', () => {
    const offered = examplesFor(fields).map((e) => e.label);

    expect(offered.length).toBeGreaterThan(0);
    expect(offered.length).toBeLessThan(examples.length);
    for (const label of offered) {
      const draft = examples.find((e) => e.label === label)!.draft;
      expect(draft.siteTypes.every((t) => t === 'radio')).toBe(true);
      expect(draft.equipment.every((e) => !e.category || e.category === 'radio')).toBe(true);
    }
    expect(examplesFor(undefined)).toEqual([]);
  });
});
