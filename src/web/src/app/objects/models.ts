import { Lifecycle } from '../shell/status';

export type ObjectType = 'site' | 'equipment' | 'cable' | 'service' | 'circuit';

/** A link to another object (ObjectRef in the API). */
export interface ObjectRef {
  type: ObjectType;
  id: number;
  code: string;
  name?: string | null;
  lifecycle?: Lifecycle | string | null;
}

export interface TerminalRef {
  terminalId: number;
  label: string;
  owner: ObjectRef;
  site: ObjectRef;
}

export interface SiteDetail {
  id: number;
  code: string;
  name: string;
  siteType: string;
  lifecycle: Lifecycle;
  /** Null when the caller's scope hides positions (#22). */
  x: number | null;
  y: number | null;
  attributes: Record<string, unknown>;
  locations: {
    id: number;
    parentId: number | null;
    kind: string;
    name: string;
    equipment: {
      id: number;
      name: string;
      model: string;
      category: string;
      lifecycle: Lifecycle;
      ports: number;
      cards: number;
    }[];
  }[];
  cables: {
    id: number;
    code: string;
    typeName: string;
    medium: string;
    conductors: number;
    lengthM: number;
    lifecycle: Lifecycle;
    otherEnd: ObjectRef;
  }[];
}

export interface EquipmentDetail {
  id: number;
  name: string;
  lifecycle: Lifecycle;
  typeKey: string;
  manufacturer: string;
  model: string;
  category: string;
  panel: { rows: number; columns: number };
  attributes: Record<string, unknown>;
  site: ObjectRef;
  locationPath: string | null;
  parent: ObjectRef | null;
  slot: string | null;
  freeSlots: string[];
  cards: { slot: string; card: ObjectRef }[];
  ports: {
    terminalId: number;
    name: string;
    type: string;
    group: string | null;
    position: number;
    row: number;
    column: number;
    connections: { kind: string; lifecycle: Lifecycle; peer: TerminalRef }[];
    circuits: number;
  }[];
}

export interface CableDetail {
  id: number;
  code: string;
  typeName: string;
  medium: string;
  conductors: number;
  conductorsInUse: number;
  lengthM: number;
  lifecycle: Lifecycle;
  attributes: Record<string, unknown>;
  a: ObjectRef;
  b: ObjectRef;
  circuits: ObjectRef[];
}

/** What depends on a cable, equipment or site (GET /api/{cables|equipment|sites}/{id}/impact, #10). */
export interface Impact {
  circuits: number;
  direct: number;
  /** Each service with the circuits that reach it: its own circuit first, down to the one hit directly. */
  services: { service: ObjectRef; path: { circuit: ObjectRef; layer: string }[] }[];
  elapsedMs: number;
  /** Affected services outside the caller's access scope (#22): counted, not named. */
  hiddenServices: number;
}

export interface ServiceDetail {
  id: number;
  code: string;
  name: string;
  serviceType: string;
  lifecycle: Lifecycle;
  attributes: Record<string, unknown>;
  circuits: { circuit: ObjectRef; a: TerminalRef | null; b: TerminalRef | null }[];
}

export interface CircuitDetail {
  id: number;
  code: string;
  layer: string;
  lifecycle: Lifecycle;
  hops: { seq: number; terminal: TerminalRef; channel: string | null }[];
  carriers: ObjectRef[];
  carried: ObjectRef[];
  services: ObjectRef[];
}

export interface ObjectSummary {
  type: ObjectType;
  id: number;
  code: string;
  name: string | null;
  lifecycle: Lifecycle;
  facts: { label: string; value: string }[];
}

export const typeLabels: Record<ObjectType, string> = {
  site: 'Site',
  equipment: 'Utrustning',
  cable: 'Kabel',
  service: 'Tjänst',
  circuit: 'Krets',
};

export const apiPath: Record<ObjectType, string> = {
  site: 'sites',
  equipment: 'equipment',
  cable: 'cables',
  service: 'services',
  circuit: 'circuits',
};

export function asLifecycle(value: string | null | undefined): Lifecycle {
  return (value ?? 'in_service') as Lifecycle;
}
