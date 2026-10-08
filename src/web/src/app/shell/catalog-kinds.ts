import { httpResource } from '@angular/common/http';
import { computed, Injectable } from '@angular/core';

/** A field of a type's attribute schema (#211). */
export interface KindAttribute {
  key: string;
  type: string;
  title: string | null;
}

/** A site type, equipment category, cable type or service type from the catalog (#208, #211). */
export interface Kind {
  key: string;
  name: string;
  roles: string[];
  /** The fields of the type's attribute schema; null when its attributes are free. */
  attributes?: KindAttribute[] | null;
}

/** GET /api/catalog/kinds */
export interface CatalogKindsResponse {
  siteTypes: Kind[];
  categories: Kind[];
  cableTypes?: Kind[];
  serviceTypes?: Kind[];
}

/** Object types whose attributes the catalog can describe. */
export type AttributeOwner = 'site' | 'cable' | 'service';

/** What the UI treats a site as. Code asks for roles, never for site type names (#208). */
export type SiteRole = 'hub' | 'aggregation' | 'access' | 'splice-point';

/** The order sites stand out in on the map and in the graph: the backbone first. */
export const siteRoleRank: readonly SiteRole[] = ['hub', 'aggregation', 'access', 'splice-point'];

/** The most prominent role of a site type, or null for a type with none or not in the catalog. */
export function siteRole(kinds: readonly Kind[], siteType: string): SiteRole | null {
  const roles = kinds.find((k) => k.key === siteType)?.roles ?? [];
  return siteRoleRank.find((r) => roles.includes(r)) ?? null;
}

/**
 * The catalog's site types and equipment categories with names and roles, loaded once. Another organisation's
 * catalog (CMDB_CATALOG_PATH, #207) brings its own names; the UI follows without code changes.
 */
@Injectable({ providedIn: 'root' })
export class CatalogKinds {
  private readonly resource = httpResource<CatalogKindsResponse>(() => '/api/catalog/kinds');

  readonly siteTypes = computed(() => this.resource.value()?.siteTypes ?? []);
  readonly categories = computed(() => this.resource.value()?.categories ?? []);
  readonly cableTypes = computed(() => this.resource.value()?.cableTypes ?? []);
  readonly serviceTypes = computed(() => this.resource.value()?.serviceTypes ?? []);

  /** The site type's name, or the key itself until the catalog has loaded. */
  siteTypeName(key: string): string {
    return this.siteTypes().find((k) => k.key === key)?.name ?? key;
  }

  categoryName(key: string): string {
    return this.categories().find((k) => k.key === key)?.name ?? key;
  }

  serviceTypeName(key: string): string {
    return this.serviceTypes().find((k) => k.key === key)?.name ?? key;
  }

  /**
   * An object's attributes as [label, value] in the order of the type's schema, then the rest by key. The label is the
   * schema's title (#211), or the key when there is none.
   */
  attributes(owner: AttributeOwner, typeKey: string, values: Record<string, unknown>) {
    return labelledAttributes(this.fields(owner, typeKey), values);
  }

  private fields(owner: AttributeOwner, typeKey: string): KindAttribute[] {
    const kinds =
      owner === 'site'
        ? this.siteTypes()
        : owner === 'cable'
          ? this.cableTypes()
          : this.serviceTypes();
    return kinds.find((k) => k.key === typeKey)?.attributes ?? [];
  }

  siteRole(siteType: string): SiteRole | null {
    return siteRole(this.siteTypes(), siteType);
  }

  categoryHas(category: string, role: string): boolean {
    return this.categories().some((k) => k.key === category && k.roles.includes(role));
  }
}

/** Attributes as [label, value]: the schema's fields first, in its order, with their titles; then any others by key. */
export function labelledAttributes(
  fields: readonly KindAttribute[],
  values: Record<string, unknown>,
): (readonly [string, string])[] {
  const text = (v: unknown) => (Array.isArray(v) ? v.join(', ') : String(v));
  const known = fields
    .filter((f) => f.key in values)
    .map((f) => [f.title ?? f.key, text(values[f.key])] as const);
  const rest = Object.keys(values)
    .filter((k) => !fields.some((f) => f.key === k))
    .sort()
    .map((k) => [k, text(values[k])] as const);
  return [...known, ...rest];
}
