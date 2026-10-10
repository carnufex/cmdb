import { portStatus, PortStatus } from '../objects/front-panel-model';
import { EquipmentDetail, ObjectRef, SiteDetail } from '../objects/models';
import { locationKindName } from '../shell/icons';

/** A node in the content tree (#17): site → location → equipment → card → port. */
export interface TreeNode {
  key: string;
  kind: 'site' | 'location' | 'equipment' | 'port';
  /** What the icon follows (#249): the site type, the location kind or the equipment category; 'card' for a card. */
  typeKey: string | null;
  label: string;
  detail: string | null;
  /** The object a click opens; ports open their equipment. */
  ref: ObjectRef | null;
  lifecycle: string | null;
  /** Ports only. */
  terminalId?: number;
  portStatus?: PortStatus;
  /** Known children; equipment children are fetched when first expanded. */
  children: TreeNode[];
  /** Equipment with ports or cards not fetched yet. */
  lazy: boolean;
}

export interface TreeRow {
  node: TreeNode;
  depth: number;
  expanded: boolean;
  expandable: boolean;
  parent: string | null;
}

export const equipmentKey = (id: number) => `equipment:${id}`;

function equipmentNode(
  e: { id: number; name: string; lifecycle: string },
  typeKey: string,
  detail: string,
  lazy: boolean,
): TreeNode {
  return {
    key: equipmentKey(e.id),
    kind: 'equipment',
    typeKey,
    label: e.name,
    detail,
    ref: { type: 'equipment', id: e.id, code: e.name },
    lifecycle: e.lifecycle,
    children: [],
    lazy,
  };
}

/** The site with its location hierarchy and the equipment in each location; ports and cards load on demand. */
export function siteTree(site: SiteDetail): TreeNode {
  const locations = new Map<number, TreeNode>();
  for (const l of site.locations) {
    locations.set(l.id, {
      key: `location:${l.id}`,
      kind: 'location',
      typeKey: l.kind,
      label: l.name,
      detail: locationKindName(l.kind),
      ref: null,
      lifecycle: null,
      children: l.equipment.map((e) =>
        equipmentNode(
          e,
          e.category,
          `${e.model}${e.cards ? ` · ${e.cards} kort` : ''}`,
          e.ports > 0 || e.cards > 0,
        ),
      ),
      lazy: false,
    });
  }
  const root: TreeNode = {
    key: `site:${site.id}`,
    kind: 'site',
    typeKey: site.siteType,
    label: site.code,
    detail: site.name,
    ref: { type: 'site', id: site.id, code: site.code, name: site.name },
    lifecycle: site.lifecycle,
    children: [],
    lazy: false,
  };
  for (const l of site.locations) {
    const node = locations.get(l.id)!;
    const parent = l.parentId !== null ? locations.get(l.parentId) : undefined;
    // Sub-locations before the equipment of their parent.
    (parent ? parent.children : root.children).push(node);
  }
  for (const node of locations.values()) {
    node.children.sort((a, b) => Number(a.kind !== 'location') - Number(b.kind !== 'location'));
  }
  return root;
}

/** An equipment's cards (by slot, themselves expandable) and then its ports in template order. */
export function equipmentChildren(detail: EquipmentDetail): TreeNode[] {
  const cards = detail.cards.map((c) =>
    equipmentNode(
      { id: c.card.id, name: c.card.code, lifecycle: String(c.card.lifecycle ?? 'in_service') },
      'card',
      `slot ${c.slot}`,
      true,
    ),
  );
  const ports = [...detail.ports]
    .sort((a, b) => a.position - b.position)
    .map<TreeNode>((p) => ({
      key: `port:${p.terminalId}`,
      kind: 'port',
      typeKey: null,
      label: p.name,
      detail: p.connections.length ? p.connections.map((c) => c.peer.label).join(', ') : null,
      ref: { type: 'equipment', id: detail.id, code: detail.name },
      lifecycle: null,
      terminalId: p.terminalId,
      portStatus: portStatus(p),
      children: [],
      lazy: false,
    }));
  return [...cards, ...ports];
}

/** The visible rows: depth first, descending only into expanded nodes. */
export function flatten(root: TreeNode, expanded: ReadonlySet<string>): TreeRow[] {
  const rows: TreeRow[] = [];
  const walk = (node: TreeNode, depth: number, parent: string | null) => {
    const expandable = node.lazy || node.children.length > 0;
    const open = expandable && expanded.has(node.key);
    rows.push({ node, depth, expanded: open, expandable, parent });
    if (open) {
      for (const child of node.children) {
        walk(child, depth + 1, node.key);
      }
    }
  };
  walk(root, 0, null);
  return rows;
}

/** Keys from the root down to the node, or null when it is not in the loaded tree. */
export function pathTo(root: TreeNode, key: string): string[] | null {
  if (root.key === key) {
    return [key];
  }
  for (const child of root.children) {
    const path = pathTo(child, key);
    if (path) {
      return [root.key, ...path];
    }
  }
  return null;
}

export function find(root: TreeNode, key: string): TreeNode | null {
  if (root.key === key) {
    return root;
  }
  for (const child of root.children) {
    const hit = find(child, key);
    if (hit) {
      return hit;
    }
  }
  return null;
}

/**
 * Tree keyboard navigation (WAI-ARIA tree view): the row index to move to and what to toggle, given the key.
 */
export function navigate(
  rows: readonly TreeRow[],
  index: number,
  key: string,
): { index: number; toggle?: 'expand' | 'collapse' } | null {
  const row = rows[index];
  if (!row) {
    return null;
  }
  switch (key) {
    case 'ArrowDown':
      return { index: Math.min(index + 1, rows.length - 1) };
    case 'ArrowUp':
      return { index: Math.max(index - 1, 0) };
    case 'Home':
      return { index: 0 };
    case 'End':
      return { index: rows.length - 1 };
    case 'ArrowRight':
      if (row.expandable && !row.expanded) {
        return { index, toggle: 'expand' };
      }
      return row.expanded ? { index: Math.min(index + 1, rows.length - 1) } : { index };
    case 'ArrowLeft':
      if (row.expanded) {
        return { index, toggle: 'collapse' };
      }
      return { index: row.parent ? rows.findIndex((r) => r.node.key === row.parent) : index };
    default:
      return null;
  }
}
