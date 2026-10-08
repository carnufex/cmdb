import { ResourceClaims } from './claims';
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
    /** A rack's height in units (#173). */
    rackUnits?: number | null;
    equipment: {
      id: number;
      name: string;
      model: string;
      category: string;
      lifecycle: Lifecycle;
      ports: number;
      cards: number;
      /** Its lowest rack unit and how many it takes (#173). */
      position?: number | null;
      units?: number | null;
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
  /** The grid, and pictures of the front and back when the catalog has them (#214). */
  panel: { rows: number; columns: number; images?: PanelImages | null };
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
    /** Reservation and plans wanting the port (#25); null when nobody claims it. */
    claims?: ResourceClaims | null;
    /** The port's area on the panel image (#214); null when the model has none. */
    box?: PortBox | null;
  }[];
}

export type PanelSide = 'front' | 'back';

/** An image in the catalog; width and height are the coordinates ports are placed in. */
export interface PanelImage {
  file: string;
  width: number;
  height: number;
}

export interface PanelImages {
  front?: PanelImage | null;
  back?: PanelImage | null;
}

export interface PortBox {
  side: PanelSide;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface CableDetail {
  id: number;
  code: string;
  typeName: string;
  /** The cable type's key in the catalog (#211). */
  typeKey?: string | null;
  medium: string;
  conductors: number;
  conductorsInUse: number;
  lengthM: number;
  lifecycle: Lifecycle;
  attributes: Record<string, unknown>;
  a: ObjectRef;
  b: ObjectRef;
  circuits: ObjectRef[];
  /** Fibres that are reserved or wanted by plans (#25). */
  claims?: { conductorId: number; number: number; claims: ResourceClaims }[] | null;
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
