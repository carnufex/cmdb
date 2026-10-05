import { HttpClient, httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  OnDestroy,
  signal,
  untracked,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { MapView } from '../map/map-view';
import { ActivePlan } from './active-plan';
import { NewOperation, PlanOperation, resolveSite, SiteChoice } from './plan-model';

/** GET /api/cables/near (#169) */
export interface NearCable {
  cable: { id: number; code: string };
  distance: number;
  at: number;
  point: { x: number; y: number };
  conductors: number;
  free: number;
  a: { id: number; code: string };
  b: { id: number; code: string };
}

interface Fields {
  siteTypes: string[];
  types: { key: string; manufacturer: string; model: string; category: string }[];
  cableTypes: { key: string; name: string; medium: string; conductors: number }[] | null;
}

interface Template {
  key: string;
  name: string;
  siteType: string;
  description: string;
  equipment: number;
  connections: number;
}

/** GET /api/plans/{id}/cables/{cableId}/termination */
interface Termination {
  cableId: number;
  sides: {
    side: 'A' | 'B';
    siteCode: string;
    equipment: string | null;
    fibres: number;
    operations: NewOperation[];
  }[];
}

type Tab = 'template' | 'site' | 'equipment' | 'cable' | 'import';

/** POST /api/plans/{id}/import (#170) */
export interface ImportResult {
  dryRun: boolean;
  sites: number;
  equipment: number;
  cables: number;
  connections: number;
  skipped: number;
  errors: { row: number; message: string }[];
  problems: number;
  elapsedMs: number;
}

/**
 * Creating objects in the active plan (#107, #26): a site from a template or bare, equipment at a site, and a cable
 * between two sites, picked by code or in the map, with a suggested termination on free ODF ports. Planned objects
 * become violet in the map.
 */
@Component({
  selector: 'cmdb-plan-create',
  imports: [FormsModule, NgTemplateOutlet],
  template: `
    <div class="tabs" role="tablist" aria-label="Nytt objekt">
      @for (t of tabs; track t.key) {
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="tab() === t.key"
          (click)="tab.set(t.key)"
        >
          {{ t.label }}
        </button>
      }
    </div>
    <ng-template #place>
      <p class="place">
        <button type="button" class="link" (click)="togglePlacing()">
          {{ mapView.placing() ? 'Avbryt' : 'Klicka i kartan för att placera' }}
        </button>
        <span class="muted">
          @if (mapView.placing()) {
            Klicka där siten ska stå.
          } @else if (mapView.placed(); as p) {
            Placeras vid {{ p.x }}, {{ p.y }} (SWEREF 99 TM).
          } @else {
            Annars placeras den vid kartans mittpunkt.
          }
        </span>
      </p>
    </ng-template>
    @switch (tab()) {
      @case ('template') {
        <form (ngSubmit)="createFromTemplate()">
          <label
            >Mall
            <select name="template" [(ngModel)]="template">
              @for (t of templates.value() ?? []; track t.key) {
                <option [value]="t.key">{{ t.name }} ({{ t.equipment }} utrustningar)</option>
              }
            </select>
          </label>
          @if (chosenTemplate(); as t) {
            <p class="muted">{{ t.description }}</p>
          }
          <label>Kod <input name="code" [(ngModel)]="code" required maxlength="50" /></label>
          <label>Namn <input name="name" [(ngModel)]="name" required maxlength="200" /></label>
          <ng-container *ngTemplateOutlet="place" />
          <button
            type="submit"
            class="action"
            [disabled]="busy() || !template || !code.trim() || !name.trim()"
          >
            Lägg till site från mall
          </button>
        </form>
      }
      @case ('site') {
        <form (ngSubmit)="createSite()">
          <label>Kod <input name="code" [(ngModel)]="code" required maxlength="50" /></label>
          <label>Namn <input name="name" [(ngModel)]="name" required maxlength="200" /></label>
          <label
            >Typ
            <select name="siteType" [(ngModel)]="siteType">
              @for (t of siteTypes(); track t) {
                <option [value]="t">{{ t }}</option>
              }
            </select>
          </label>
          <ng-container *ngTemplateOutlet="place" />
          <button type="submit" class="action" [disabled]="busy() || !code.trim() || !name.trim()">
            Lägg till site
          </button>
          @if (nearCables.value()?.length) {
            <div class="near" aria-label="Kablar i närheten">
              <p class="muted">Kablar i närheten: sätt in siten i en av dem i stället (#168).</p>
              <ul>
                @for (n of nearCables.value()!; track n.cable.id) {
                  <li>
                    <span class="mono">{{ n.cable.code }}</span>
                    <span class="muted"
                      >{{ n.distance }} m · {{ n.free }} av {{ n.conductors }} ledare lediga ·
                      {{ n.a.code }}–{{ n.b.code }}</span
                    >
                    <button
                      type="button"
                      class="link"
                      [disabled]="busy() || !code.trim() || !name.trim()"
                      (click)="insertInto(n)"
                    >
                      Sätt in i kabeln
                    </button>
                  </li>
                }
              </ul>
            </div>
          }
        </form>
      }
      @case ('equipment') {
        <form (ngSubmit)="createEquipment()">
          <label
            >Site (kod) <input name="site" [(ngModel)]="site" list="plan-sites" required
          /></label>
          <label
            >Modell
            <select name="typeKey" [(ngModel)]="typeKey">
              @for (t of models(); track t.key) {
                <option [value]="t.key">
                  {{ t.manufacturer }} {{ t.model }} ({{ t.category }})
                </option>
              }
            </select>
          </label>
          <label>Namn <input name="name" [(ngModel)]="name" required maxlength="200" /></label>
          <label
            >Rack
            <input name="rack" [(ngModel)]="rack" placeholder="Sitens första rack" maxlength="100"
          /></label>
          <label
            >Rum
            <input
              name="room"
              [(ngModel)]="room"
              placeholder="Bara för ett nytt rack"
              maxlength="100"
          /></label>
          <label
            >Kritikalitet
            <select name="criticality" [(ngModel)]="criticality">
              <option value="">Ingen</option>
              @for (l of criticalityLevels(); track l.level) {
                <option [value]="l.level">{{ l.level }} · {{ l.name }}</option>
              }
            </select>
          </label>
          <label
            >Position (U)
            <input
              name="position"
              type="number"
              min="1"
              max="60"
              [(ngModel)]="rackUnit"
              placeholder="Överst i racket"
          /></label>
          <button
            type="submit"
            class="action"
            [disabled]="busy() || !site.trim() || !name.trim() || !typeKey"
          >
            Lägg till utrustning
          </button>
        </form>
      }
      @case ('cable') {
        <form (ngSubmit)="createCable()">
          <label
            >Från site (kod) <input name="a" [(ngModel)]="siteA" list="plan-sites" required
          /></label>
          <label
            >Till site (kod) <input name="b" [(ngModel)]="siteB" list="plan-sites" required
          /></label>
          <button type="button" class="action" (click)="togglePicking()">
            {{ mapView.picking() ? 'Sluta välja i kartan' : 'Välj siterna i kartan' }}
          </button>
          @if (mapView.picking()) {
            <p class="muted" role="status">
              Klicka på {{ siteA ? 'siten i andra änden' : 'siten i första änden' }} i kartan.
            </p>
          }
          <label
            >Kabeltyp
            <select name="cableType" [(ngModel)]="cableType">
              @for (t of cableTypes(); track t.key) {
                <option [value]="t.key">{{ t.name }}</option>
              }
            </select>
          </label>
          <button
            type="submit"
            class="action"
            [disabled]="busy() || !siteA.trim() || !siteB.trim() || !cableType"
          >
            Lägg till kabel
          </button>
        </form>
        @if (termination(); as t) {
          <div class="suggestion" role="status">
            <p>Förslag till terminering:</p>
            <ul>
              @for (s of t.sides; track s.side) {
                <li>
                  {{ s.side }} ({{ s.siteCode }}):
                  @if (s.fibres > 0) {
                    {{ s.fibres }} fibrer mot {{ s.equipment }}
                  } @else {
                    ingen ledig ODF
                  }
                </li>
              }
            </ul>
            <button
              type="button"
              class="action"
              [disabled]="busy() || !terminable()"
              (click)="terminate()"
            >
              Terminera
            </button>
            <button type="button" class="action" (click)="termination.set(null)">Hoppa över</button>
          </div>
        }
      }
    }
    @if (tab() === 'import') {
      <form class="import" (submit)="$event.preventDefault()">
        <p class="muted">
          CSV med kolumnerna <span class="mono">kind, code, name, template</span> eller
          <span class="mono">siteType</span>, <span class="mono">x, y</span> (SWEREF 99 TM) eller
          <span class="mono">lat, lon</span>, och för kablar
          <span class="mono">a, b, cableType</span>. GeoJSON: punkter blir siter och linjer kablar
          med sin sträckning. Allt kontrolleras först; finns ett fel läggs inget till.
        </p>
        <label
          >Fil
          <input type="file" accept=".csv,.txt,.geojson,.json" (change)="readFile($event)" />
        </label>
        <label
          >Eller klistra in
          <textarea
            name="importText"
            rows="5"
            [value]="importText()"
            (input)="importText.set($any($event.target).value)"
          ></textarea>
        </label>
        <p class="buttons">
          <button
            type="button"
            class="action"
            [disabled]="busy() || !importText().trim()"
            (click)="runImport(true)"
          >
            Kontrollera
          </button>
          <button
            type="button"
            class="action"
            [disabled]="busy() || !importReady()"
            (click)="runImport(false)"
          >
            Importera
          </button>
        </p>
        @if (importResult(); as r) {
          <div class="result" role="status">
            <p>
              {{ r.dryRun ? 'Skulle lägga till' : 'Lade till' }} {{ r.sites }} siter,
              {{ r.equipment }} utrustningar, {{ r.connections }} kopplingar och
              {{ r.cables }} kablar
              @if (r.skipped) {
                · {{ r.skipped }} fanns redan i planen
              }
              <span class="muted">({{ r.elapsedMs }} ms)</span>
            </p>
            @if (r.errors.length) {
              <ul class="errors">
                @for (e of r.errors; track $index) {
                  <li>Rad {{ e.row }}: {{ e.message }}</li>
                }
              </ul>
            }
          </div>
        }
      </form>
    }
    <datalist id="plan-sites">
      @for (s of plannedSites(); track s.id) {
        <option [value]="s.code">{{ s.name }} (planerad)</option>
      }
    </datalist>
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }
    .near ul {
      margin: 0;
      padding: 0;
      list-style: none;
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      font-size: var(--text-sm);
    }
    .near li {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--space-2);
    }
    .import textarea {
      font-family: var(--font-mono);
      font-size: var(--text-xs);
    }
    .import .buttons {
      display: flex;
      gap: var(--space-2);
      margin: 0;
    }
    .import .errors {
      margin: var(--space-1) 0 0;
      padding-left: var(--space-4);
      color: var(--status-conflict);
      font-size: var(--text-sm);
      max-height: 160px;
      overflow-y: auto;
    }
    .place {
      display: flex;
      flex-direction: column;
      gap: 2px;
      margin: 0;
    }
    .link {
      align-self: flex-start;
      padding: 0;
      border: 0;
      background: none;
      color: var(--action);
      font: inherit;
      font-size: var(--text-sm);
      font-weight: 600;
      cursor: pointer;
    }
    .tabs {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-1);
    }
    .tabs button {
      border: var(--line);
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      font: inherit;
      font-size: var(--text-sm);
      padding: 2px var(--space-2);
      cursor: pointer;
      &[aria-selected='true'] {
        color: var(--text);
        background: var(--surface-2);
      }
    }
    form {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }
    label {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      font-size: var(--text-sm);
    }
    input,
    select {
      font: inherit;
      padding: var(--space-1) var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      color: var(--text);
    }
    .action {
      align-self: flex-start;
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
      &:hover {
        background: var(--surface-2);
      }
    }
    .suggestion {
      border-left: 3px solid var(--status-planned);
      padding-left: var(--space-2);
      font-size: var(--text-sm);
      p,
      ul {
        margin: 0 0 var(--space-1);
      }
      ul {
        padding-left: var(--space-4);
      }
    }
    .muted {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .error {
      margin: 0;
      color: var(--status-conflict);
      font-size: var(--text-sm);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanCreateComponent implements OnDestroy {
  private readonly http = inject(HttpClient);
  protected readonly mapView = inject(MapView);
  private readonly active = inject(ActivePlan);

  protected readonly tabs: { key: Tab; label: string }[] = [
    { key: 'template', label: 'Från mall' },
    { key: 'import', label: 'Import' },
    { key: 'site', label: 'Ny site' },
    { key: 'equipment', label: 'Ny utrustning' },
    { key: 'cable', label: 'Ny kabel' },
  ];
  protected readonly tab = signal<Tab>('template');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly termination = signal<Termination | null>(null);
  protected readonly terminable = computed(() =>
    (this.termination()?.sides ?? []).some((s) => s.fibres > 0),
  );

  protected readonly fields = httpResource<Fields>(() => '/api/query/fields');
  protected readonly templates = httpResource<Template[]>(() => '/api/templates');
  protected readonly siteTypes = computed(() => this.fields.value()?.siteTypes ?? ['radio']);
  protected readonly models = computed(() =>
    (this.fields.value()?.types ?? []).filter((t) => t.category !== 'card'),
  );
  protected readonly cableTypes = computed(() => this.fields.value()?.cableTypes ?? []);
  protected readonly plannedSites = computed<SiteChoice[]>(
    () => this.active.view.value()?.planned?.sites ?? [],
  );
  protected readonly chosenTemplate = computed(() => {
    this.templateKey();
    return (this.templates.value() ?? []).find((t) => t.key === this.template) ?? null;
  });

  protected code = '';
  protected name = '';
  protected siteType = 'radio';
  protected site = '';
  protected rack = '';
  protected room = '';
  protected rackUnit: number | null = null;
  protected criticality = '';
  protected readonly criticalityLevels = computed(
    () => this.schemas.value()?.find((s) => s.key === 'criticality')?.levels ?? [],
  );
  private readonly schemas = httpResource<
    { key: string; levels: { level: number; name: string }[] }[]
  >(() => '/api/classifications/schemas');
  protected typeKey = '';
  protected siteA = '';
  protected siteB = '';
  protected cableType = '';
  private readonly templateKey = signal('');
  protected get template(): string {
    return this.templateKey();
  }
  protected set template(value: string) {
    this.templateKey.set(value);
  }

  constructor() {
    // Sites picked in the map fill the cable's ends in turn.
    effect(() => {
      const picked = this.mapView.picked();
      if (!picked || !untracked(() => this.mapView.picking())) {
        return;
      }
      if (!this.siteA) {
        this.siteA = picked.code;
      } else {
        this.siteB = picked.code;
        this.mapView.picking.set(false);
      }
    });
  }

  ngOnDestroy(): void {
    this.mapView.picking.set(false);
    this.mapView.placing.set(false);
    this.mapView.placed.set(null);
  }

  protected togglePlacing(): void {
    this.mapView.picking.set(false);
    this.mapView.placing.set(!this.mapView.placing());
  }

  /** Cables near the placed point (#169): a new site there can go into one of them (#168). */
  protected readonly nearCables = httpResource<NearCable[]>(() => {
    const p = this.mapView.placed();
    return p && this.tab() === 'site' ? `/api/cables/near?x=${p.x}&y=${p.y}&radius=500` : undefined;
  });

  /** The new site on the cable, at the point the placed one projects to, and the cable split there. */
  protected async insertInto(near: NearCable): Promise<void> {
    await this.run(async () => {
      const site = await this.active.add({
        kind: 'create_site',
        code: this.code.trim(),
        name: this.name.trim(),
        siteType: this.siteType,
        x: near.point.x,
        y: near.point.y,
      });
      await this.active.add({
        kind: 'split_cable',
        cableId: near.cable.id,
        siteId: site.target!.id,
        terminate: [],
      });
      this.code = '';
      this.name = '';
      this.mapView.placed.set(null);
    });
  }

  /** Where a new site goes (#167): where it was placed in the map, otherwise the map's centre. */
  private position(): { x: number; y: number } | null {
    return this.mapView.placed() ?? this.center();
  }

  protected togglePicking(): void {
    const on = !this.mapView.picking();
    if (on) {
      this.siteA = '';
      this.siteB = '';
    }
    this.mapView.picking.set(on);
  }

  protected async createFromTemplate(): Promise<void> {
    const center = this.position();
    if (!center) {
      return;
    }
    await this.run(async () => {
      await firstValueFrom(
        this.http.post(`/api/plans/${this.active.id()}/templates`, {
          templateKey: this.template,
          code: this.code.trim(),
          name: this.name.trim(),
          x: Math.round(center.x),
          y: Math.round(center.y),
        }),
      );
      this.active.changed();
      this.code = '';
      this.name = '';
      this.mapView.placed.set(null);
    });
  }

  protected async createSite(): Promise<void> {
    const center = this.position();
    if (!center) {
      return;
    }
    await this.run(async () => {
      await this.active.add({
        kind: 'create_site',
        code: this.code.trim(),
        name: this.name.trim(),
        siteType: this.siteType,
        x: Math.round(center.x),
        y: Math.round(center.y),
      });
      this.code = '';
      this.name = '';
      this.mapView.placed.set(null);
    });
  }

  protected async createEquipment(): Promise<void> {
    await this.run(async () => {
      const siteId = await this.siteId(this.site);
      const equipment = await this.active.add({
        kind: 'create_equipment',
        siteId,
        typeKey: this.typeKey,
        name: this.name.trim(),
        // Rack, room and position (#173): left out, the site's first rack and the top of what it holds.
        ...(this.rack.trim() ? { rack: this.rack.trim() } : {}),
        ...(this.room.trim() ? { room: this.room.trim() } : {}),
        ...(this.rackUnit ? { position: Number(this.rackUnit) } : {}),
      });
      // The level of what is new (#179): the plan then shows what it does to the site's requirements.
      if (this.criticality) {
        await this.active.add({
          kind: 'set_classification',
          type: 'equipment',
          objectId: equipment.target!.id,
          schema: 'criticality',
          level: Number(this.criticality),
        });
      }
      this.rackUnit = null;
      this.criticality = '';
      this.name = '';
    });
  }

  protected async createCable(): Promise<void> {
    await this.run(async () => {
      const [aSiteId, bSiteId] = [await this.siteId(this.siteA), await this.siteId(this.siteB)];
      const cable: PlanOperation = await this.active.add({
        kind: 'create_cable',
        aSiteId,
        bSiteId,
        typeKey: this.cableType,
      });
      // A new cable comes with a suggested termination on free ODF ports at both ends.
      this.termination.set(
        await firstValueFrom(
          this.http.get<Termination>(
            `/api/plans/${this.active.id()}/cables/${cable.target!.id}/termination`,
          ),
        ),
      );
    });
  }

  protected async terminate(): Promise<void> {
    const t = this.termination();
    if (!t) {
      return;
    }
    await this.run(async () => {
      await firstValueFrom(
        this.http.post(`/api/plans/${this.active.id()}/operations/batch`, {
          operations: t.sides.flatMap((s) => s.operations),
        }),
      );
      this.active.changed();
      this.termination.set(null);
    });
  }

  private center(): { x: number; y: number } | null {
    const center = this.mapView.center();
    if (!center) {
      this.error.set('Kartan har ingen mittpunkt än. Öppna kartan först.');
    }
    return center;
  }

  /** A site by code: planned in the plan, or an exact hit in the quick search. */
  private async siteId(code: string): Promise<number> {
    const id = await resolveSite(code, this.plannedSites(), (q) =>
      firstValueFrom(
        this.http.get<{ type: string; id: number; code: string }[]>(
          `/api/search?q=${encodeURIComponent(q)}&limit=10`,
        ),
      ),
    );
    if (id === null) {
      throw new Error(`Hittade ingen site med koden ${code.trim()}.`);
    }
    return id;
  }

  // Bulk import into the plan (#170): checked first, then all or nothing.
  protected readonly importText = signal('');
  protected readonly importResult = signal<ImportResult | null>(null);
  private importChecked = '';

  /** A clean check of exactly this text, so importing it does what the check said. */
  protected importReady(): boolean {
    const r = this.importResult();
    return (
      r !== null && r.dryRun && r.errors.length === 0 && this.importChecked === this.importText()
    );
  }

  protected async readFile(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) {
      this.importText.set(await file.text());
      this.importResult.set(null);
    }
  }

  protected async runImport(dryRun: boolean): Promise<void> {
    const content = this.importText();
    const format = content.trimStart().startsWith('{') ? 'geojson' : 'csv';
    await this.run(async () => {
      const result = await firstValueFrom(
        this.http.post<ImportResult>(`/api/plans/${this.active.id()}/import`, {
          format,
          content,
          dryRun,
        }),
      );
      this.importResult.set(result);
      this.importChecked = dryRun ? content : '';
      if (!dryRun) {
        this.active.changed();
      }
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch (e: unknown) {
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.error.set(
        body?.detail ??
          (Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ||
            (e instanceof Error ? e.message : 'Det gick inte.')),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
