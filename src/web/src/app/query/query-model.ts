import { Lifecycle } from '../shell/status';

/** GET /api/query/fields */
export interface QueryFields {
  siteTypes: string[];
  lifecycles: Lifecycle[];
  serviceTypes: string[];
  categories: { key: string; attributes: AttributeField[] }[];
  types: { key: string; manufacturer: string; model: string; category: string }[];
}

export interface AttributeField {
  key: string;
  type: 'string' | 'number';
  values: (string | number)[] | null;
}

export type Op = 'eq' | 'neq' | 'gt' | 'gte' | 'lt' | 'lte' | 'prefix' | 'contains' | 'exists';

/** One equipment condition as edited in the form; empty strings mean "any". */
export interface EquipmentDraft {
  category: string;
  typeKey: string;
  key: string;
  op: Op;
  value: string;
  minCount: number;
}

export interface QueryDraft {
  siteTypes: string[];
  lifecycles: string[];
  serviceTypes: string[];
  equipment: EquipmentDraft[];
}

/** POST /api/query/sites */
export interface SiteQueryResult {
  total: number;
  sites: {
    id: number;
    code: string;
    name: string;
    siteType: string;
    lifecycle: Lifecycle;
    /** Null when the caller's scope hides positions (#22). */
    x: number | null;
    y: number | null;
    matching: number | null;
  }[];
  points: [number, number, number][];
  extent: number[] | null;
  elapsedMs: number;
}

export const opLabels: Record<Op, string> = {
  eq: '=',
  neq: '≠',
  gt: '>',
  gte: '≥',
  lt: '<',
  lte: '≤',
  prefix: 'börjar med',
  contains: 'innehåller',
  exists: 'finns',
};

export const numericOps: Op[] = ['eq', 'neq', 'gt', 'gte', 'lt', 'lte', 'exists'];
export const textOps: Op[] = ['eq', 'neq', 'prefix', 'contains', 'exists'];

export const siteTypeLabels: Record<string, string> = {
  hub: 'Nav',
  aggregation: 'Aggregering',
  radio: 'Radiosite',
  cabinet: 'Skåp',
  splice: 'Skarvpunkt',
};

export const categoryLabels: Record<string, string> = {
  switch: 'Switch',
  router: 'Router',
  card: 'Kort',
  radio: 'Radio',
  antenna: 'Antenn',
  transmission: 'Transmission',
  odf: 'ODF',
  patch: 'Patchpanel',
  power: 'Kraft',
};

export const serviceTypeLabels: Record<string, string> = {
  'mobile-backhaul': 'Mobil backhaul',
  ethernet: 'Ethernet',
  'core-link': 'Stamnätslänk',
};

export function emptyEquipment(): EquipmentDraft {
  return { category: '', typeKey: '', key: '', op: 'eq', value: '', minCount: 1 };
}

/** Ready-made questions that show what the search can do. */
export const examples: { label: string; draft: QueryDraft }[] = [
  {
    label: 'Radiositer med 3500 MHz',
    draft: {
      siteTypes: ['radio'],
      lifecycles: [],
      serviceTypes: [],
      equipment: [
        { ...emptyEquipment(), category: 'radio', key: 'bandMHz', op: 'eq', value: '3500' },
      ],
    },
  },
  {
    label: 'Minst tre antenner',
    draft: {
      siteTypes: [],
      lifecycles: [],
      serviceTypes: [],
      equipment: [{ ...emptyEquipment(), category: 'antenna', minCount: 3 }],
    },
  },
  {
    label: 'Sändeffekt över 60 W',
    draft: {
      siteTypes: [],
      lifecycles: [],
      serviceTypes: [],
      equipment: [
        { ...emptyEquipment(), category: 'radio', key: 'transmitPowerW', op: 'gt', value: '60' },
      ],
    },
  },
  {
    label: 'Switchar med firmware 1.x',
    draft: {
      siteTypes: [],
      lifecycles: [],
      serviceTypes: [],
      equipment: [
        { ...emptyEquipment(), category: 'switch', key: 'firmware', op: 'prefix', value: '1.' },
      ],
    },
  },
  {
    label: 'Planerade skåp med ethernet',
    draft: {
      siteTypes: ['cabinet'],
      lifecycles: ['planned'],
      serviceTypes: ['ethernet'],
      equipment: [],
    },
  },
  {
    label: 'Nav med minst tre linjekort',
    draft: {
      siteTypes: ['hub'],
      lifecycles: [],
      serviceTypes: [],
      equipment: [{ ...emptyEquipment(), typeKey: 'acme-lc-24x', minCount: 3 }],
    },
  },
];

/** Turns the form into the API request. Blank parts are left out; numbers are sent as numbers. */
export function toRequest(draft: QueryDraft, fields: QueryFields | undefined, limit = 200) {
  const attributeType = (category: string, key: string) =>
    fields?.categories.find((c) => c.key === category)?.attributes.find((a) => a.key === key)
      ?.type ??
    fields?.categories.flatMap((c) => c.attributes).find((a) => a.key === key)?.type ??
    'string';
  return {
    siteTypes: draft.siteTypes.length ? draft.siteTypes : undefined,
    lifecycles: draft.lifecycles.length ? draft.lifecycles : undefined,
    serviceTypes: draft.serviceTypes.length ? draft.serviceTypes : undefined,
    equipment: draft.equipment
      .filter((e) => e.category || e.typeKey || e.key)
      .map((e) => ({
        category: e.category || undefined,
        typeKey: e.typeKey || undefined,
        minCount: e.minCount > 1 ? e.minCount : undefined,
        attribute: e.key
          ? {
              key: e.key,
              op: e.op,
              value:
                e.op === 'exists'
                  ? undefined
                  : attributeType(e.category, e.key) === 'number' &&
                      e.op !== 'prefix' &&
                      e.op !== 'contains'
                    ? Number(e.value)
                    : e.value,
            }
          : undefined,
      })),
    limit,
  };
}
