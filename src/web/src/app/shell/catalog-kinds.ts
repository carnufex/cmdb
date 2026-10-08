import { httpResource } from '@angular/common/http';
import { computed, Injectable } from '@angular/core';

/** A site type or equipment category from the catalog (#208). */
export interface Kind {
  key: string;
  name: string;
  roles: string[];
}

/** GET /api/catalog/kinds */
export interface CatalogKindsResponse {
  siteTypes: Kind[];
  categories: Kind[];
}

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

  /** The site type's name, or the key itself until the catalog has loaded. */
  siteTypeName(key: string): string {
    return this.siteTypes().find((k) => k.key === key)?.name ?? key;
  }

  categoryName(key: string): string {
    return this.categories().find((k) => k.key === key)?.name ?? key;
  }

  siteRole(siteType: string): SiteRole | null {
    return siteRole(this.siteTypes(), siteType);
  }

  categoryHas(category: string, role: string): boolean {
    return this.categories().some((k) => k.key === category && k.roles.includes(role));
  }
}
