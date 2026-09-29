import { describe, expect, it } from 'vitest';
import { EquipmentDetail, SiteDetail } from '../objects/models';
import { equipmentChildren, flatten, navigate, pathTo, siteTree } from './tree-model';

const site = {
  id: 1,
  code: 'HUB-001',
  name: 'Nav 1',
  siteType: 'hub',
  lifecycle: 'in_service',
  x: 0,
  y: 0,
  attributes: {},
  cables: [],
  locations: [
    { id: 10, parentId: null, kind: 'building', name: 'Byggnad A', equipment: [] },
    {
      id: 11,
      parentId: 10,
      kind: 'rack',
      name: 'Rack 1',
      equipment: [
        {
          id: 100,
          name: 'CR-8 1',
          model: 'CR-8',
          category: 'router',
          lifecycle: 'in_service',
          ports: 2,
          cards: 1,
        },
        {
          id: 101,
          name: 'PDU 1',
          model: 'PDU',
          category: 'power',
          lifecycle: 'in_service',
          ports: 0,
          cards: 0,
        },
      ],
    },
  ],
} as unknown as SiteDetail;

const chassis = {
  id: 100,
  name: 'CR-8 1',
  cards: [
    { slot: '1', card: { type: 'equipment', id: 200, code: 'LC-24X 1', lifecycle: 'in_service' } },
  ],
  ports: [
    { terminalId: 2, name: 'mgmt', position: 2, connections: [] },
    { terminalId: 1, name: 'con', position: 1, connections: [] },
  ],
} as unknown as EquipmentDetail;

describe('content tree', () => {
  it('nests locations and lists equipment, loading ports and cards on demand', () => {
    const root = siteTree(site);
    expect(flatten(root, new Set()).map((r) => r.node.key)).toEqual(['site:1']);

    const rows = flatten(root, new Set(['site:1', 'location:10', 'location:11']));
    expect(rows.map((r) => [r.node.key, r.depth])).toEqual([
      ['site:1', 0],
      ['location:10', 1],
      ['location:11', 2],
      ['equipment:100', 3],
      ['equipment:101', 3],
    ]);
    expect(rows[3].expandable).toBe(true); // has ports and a card, not loaded yet
    expect(rows[4].expandable).toBe(false);

    const chassisNode = root.children[0].children[0].children[0];
    chassisNode.children = equipmentChildren(chassis);
    chassisNode.lazy = false;
    expect(chassisNode.children.map((c) => c.label)).toEqual(['LC-24X 1', 'con', 'mgmt']);
    expect(pathTo(root, 'port:2')).toEqual([
      'site:1',
      'location:10',
      'location:11',
      'equipment:100',
      'port:2',
    ]);
    expect(pathTo(root, 'port:9')).toBeNull();
  });

  it('moves like a tree view with the keyboard', () => {
    const root = siteTree(site);
    const rows = flatten(root, new Set(['site:1', 'location:10']));

    expect(navigate(rows, 0, 'ArrowDown')).toEqual({ index: 1 });
    expect(navigate(rows, 2, 'ArrowRight')).toEqual({ index: 2, toggle: 'expand' });
    expect(navigate(rows, 1, 'ArrowRight')).toEqual({ index: 2 });
    expect(navigate(rows, 1, 'ArrowLeft')).toEqual({ index: 1, toggle: 'collapse' });
    expect(navigate(rows, 2, 'ArrowLeft')).toEqual({ index: 1 });
    expect(navigate(rows, 0, 'End')).toEqual({ index: 2 });
    expect(navigate(rows, 0, 'x')).toBeNull();
  });
});
