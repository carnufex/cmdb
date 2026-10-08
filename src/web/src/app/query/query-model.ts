import { Lifecycle } from '../shell/status';

/** GET /api/query/fields */
export interface QueryFields {
  siteTypes: string[];
  lifecycles: Lifecycle[];
  serviceTypes: string[];
  categories: { key: string; attributes: AttributeField[] }[];
  types: { key: string; manufacturer: string; model: string; category: string }[];
  /** Site types with the fields of their attribute schemas (#211). */
  siteTypeDetails?: { key: string; name: string; attributes: AttributeField[] | null }[] | null;
}

export interface AttributeField {
  key: string;
  type: 'string' | 'number';
  values: (string | number)[] | null;
  /** The schema's title (#211), when it has one. */
  title?: string | null;
}

/** A test on the site's own attributes (#211); an empty key means none. */
export interface SiteAttributeDraft {
  key: string;
  op: Op;
  value: string;
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
  siteAttribute?: SiteAttributeDraft;
}

/** Every attribute field the site types' schemas have, once per key. */
export function siteAttributeFields(fields: QueryFields | undefined): AttributeField[] {
  const all = (fields?.siteTypeDetails ?? []).flatMap((t) => t.attributes ?? []);
  return [...new Map(all.map((a) => [a.key, a])).values()];
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

/**
 * The examples that make sense for the loaded catalog: each site type, category, model and attribute they name must
 * exist. Another organisation's catalog (#207, #208) then shows only the ones that apply instead of empty searches.
 */
export function examplesFor(fields: QueryFields | undefined): typeof examples {
  if (!fields) {
    return [];
  }
  const attributes = new Set(fields.categories.flatMap((c) => c.attributes.map((a) => a.key)));
  const fits = (e: EquipmentDraft) =>
    (!e.category || fields.categories.some((c) => c.key === e.category)) &&
    (!e.typeKey || fields.types.some((t) => t.key === e.typeKey)) &&
    (!e.key || attributes.has(e.key));
  return examples.filter(
    (x) =>
      x.draft.siteTypes.every((t) => fields.siteTypes.includes(t)) && x.draft.equipment.every(fits),
  );
}

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
    siteAttributes: draft.siteAttribute?.key
      ? [siteAttributeRequest(draft.siteAttribute, siteAttributeFields(fields))]
      : undefined,
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

function siteAttributeRequest(a: SiteAttributeDraft, fields: AttributeField[]) {
  const numeric =
    fields.find((f) => f.key === a.key)?.type === 'number' &&
    a.op !== 'prefix' &&
    a.op !== 'contains';
  return {
    key: a.key,
    op: a.op,
    value: a.op === 'exists' ? undefined : numeric ? Number(a.value) : a.value,
  };
}
