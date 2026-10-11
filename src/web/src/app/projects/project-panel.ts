import { HttpClient, httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MapView } from '../map/map-view';
import { ActivePlan } from '../plans/active-plan';
import { CurrentUser } from '../shell/current-user';
import { Lifecycle, StatusComponent } from '../shell/status';
import { Tools } from '../shell/tools';

/** GET /api/projects (#293) */
export interface ProjectSummary {
  id: number;
  name: string;
  owner: string;
  stages: number;
  applied: number;
  updatedAt: string;
}

/** GET /api/projects/{id} */
export interface ProjectDetail {
  id: number;
  name: string;
  description: string;
  owner: string;
  createdBy: string;
  area: number[][][] | null;
  extent: number[] | null;
  stages: ProjectStage[];
  hiddenStages: number;
  changes: number;
  cableMetres: number;
  cost: number;
  currency: string;
  nextStep: string;
  updatedAt: string;
}

export interface ProjectStage {
  seq: number;
  plan: { id: number; name: string; status: string; flag: string | null };
  changes: number;
  problems: number;
  cableMetres: number;
  cost: number;
  unpriced: boolean;
}

/** A stage's state as the status dot shows it: applied like in service, cancelled like removed, a flagged or broken draft as a conflict. */
export function stageLifecycle(stage: ProjectStage): Lifecycle {
  return stage.plan.status === 'applied'
    ? 'in_service'
    : stage.plan.status === 'cancelled'
      ? 'removed'
      : stage.problems > 0 || stage.plan.flag
        ? 'conflict'
        : 'planned';
}

export function stageStatusText(stage: ProjectStage): string {
  return stage.plan.status === 'applied'
    ? 'Införd'
    : stage.plan.status === 'cancelled'
      ? 'Avbruten'
      : stage.problems > 0
        ? `${stage.problems} passar inte`
        : stage.plan.flag
          ? 'Flaggad'
          : 'Utkast';
}

/** GET /api/projects/{id}/node-suggestions (#294) */
export interface NodeSuggestions {
  capacity: number;
  servicePoints: number;
  units: number;
  nodes: NodeSuggestion[];
  elapsedMs: number;
}

export interface NodeSuggestion {
  number: number;
  x: number;
  y: number;
  snappedTo: { type: string; id: number; code: string; name: string | null } | null;
  servicePoints: number;
  units: number;
  reachM: number;
  overCapacity: boolean;
  servicePointIds: number[];
}

/** A suggested node's site code: the project and the node's number, e.g. P12-N03. */
export function nodeCode(project: number, n: number): string {
  return `P${project}-N${String(n).padStart(2, '0')}`;
}

/** An amount in whole units of the currency, with spaces between thousands, as Swedish does. */
export function costText(amount: number, currency: string): string {
  return `${Math.round(amount).toLocaleString('sv-SE')} ${currency}`;
}

/**
 * Projects (#293, ADR-0021): an area built out in stages. The list, and a project with its area outlined in the map,
 * its stages as plans with status as dot and text, their changes, new cable and cost (#291), and what to do next. *Visa*
 * opens a stage's plan; *Ny etapp* adds a draft that builds on the stage before it.
 */
@Component({
  selector: 'cmdb-project-panel',
  imports: [StatusComponent, DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="head">
      @if (selected() !== null) {
        <button type="button" class="text-button" (click)="select(null)">← Alla projekt</button>
      } @else {
        <h2>Projekt</h2>
      }
      <button type="button" class="close" aria-label="Stäng projekt" (click)="tools.close()">
        ×
      </button>
    </header>

    @if (selected() === null) {
      @if (projects.hasValue()) {
        @if (projects.value().length) {
          <ul class="projects">
            @for (p of projects.value(); track p.id) {
              <li>
                <button type="button" class="row" (click)="select(p.id)">
                  <span class="name">{{ p.name }}</span>
                  <span class="muted"
                    >{{ p.applied }} av {{ p.stages }} etapper införda · {{ p.owner }}</span
                  >
                </button>
              </li>
            }
          </ul>
        } @else {
          <p class="muted">Inga projekt i ditt omfång än.</p>
        }
      } @else if (projects.error()) {
        <p class="error">Projekten kunde inte hämtas.</p>
      }
      @if (canWrite()) {
        <form class="create" (submit)="$event.preventDefault(); create()">
          <h3>Nytt projekt</h3>
          <label
            >Namn
            <input
              name="name"
              [value]="newName()"
              (input)="newName.set($any($event.target).value)"
              maxlength="200"
            />
          </label>
          <p class="buttons">
            <button
              type="button"
              class="action"
              (click)="drawArea()"
              [disabled]="map.drawingArea()"
            >
              {{ newArea() ? 'Rita om området' : 'Rita området i kartan' }}
            </button>
            @if (newArea(); as a) {
              <span class="muted">{{ a.length }} hörn</span>
            }
          </p>
          @if (map.drawingArea()) {
            <p class="hint" role="status">
              Klicka ut hörnen i kartan och dubbelklicka på det sista.
            </p>
          }
          <button
            type="submit"
            class="primary"
            [disabled]="busy() || !newName().trim() || !newArea()"
          >
            Skapa projekt
          </button>
        </form>
      }
    } @else if (project.hasValue()) {
      @let p = project.value();
      <section class="summary">
        <h2>{{ p.name }}</h2>
        @if (p.description) {
          <p>{{ p.description }}</p>
        }
        <p class="muted">Ansvarig {{ p.owner }}</p>
        <p class="next" role="status"><strong>Nästa steg:</strong> {{ p.nextStep }}</p>
        <p class="buttons">
          @if (p.extent) {
            <button type="button" class="text-button" (click)="showArea(p)">Visa området</button>
          }
          @if (canWrite()) {
            <button
              type="button"
              class="text-button"
              (click)="drawArea()"
              [disabled]="map.drawingArea()"
            >
              Rita om området
            </button>
          }
        </p>
        @if (map.drawingArea()) {
          <p class="hint" role="status">Klicka ut hörnen i kartan och dubbelklicka på det sista.</p>
        }
      </section>
      <section>
        <h3>Etapper</h3>
        @if (p.stages.length) {
          <table class="rows">
            <thead>
              <tr>
                <th>#</th>
                <th>Etapp</th>
                <th>Läge</th>
                <th class="num">Ändringar</th>
                <th class="num">Ny kabel</th>
                <th class="num">Kostnad</th>
                <th><span class="visually-hidden">Visa</span></th>
              </tr>
            </thead>
            <tbody>
              @for (s of p.stages; track s.plan.id; let i = $index) {
                <tr>
                  <td>{{ i + 1 }}</td>
                  <td>{{ s.plan.name }}</td>
                  <td><cmdb-status [value]="lifecycle(s)" [text]="statusText(s)" /></td>
                  <td class="num">{{ s.changes }}</td>
                  <td class="num">{{ s.cableMetres / 1000 | number: '1.1-1' }} km</td>
                  <td class="num">{{ cost(s.cost, p.currency) }}{{ s.unpriced ? '*' : '' }}</td>
                  <td>
                    <button type="button" class="text-button" (click)="show(s.plan.id)">
                      Visa
                    </button>
                  </td>
                </tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <td></td>
                <td>Totalt</td>
                <td></td>
                <td class="num">{{ p.changes }}</td>
                <td class="num">{{ p.cableMetres / 1000 | number: '1.1-1' }} km</td>
                <td class="num">{{ cost(p.cost, p.currency) }}</td>
                <td></td>
              </tr>
            </tfoot>
          </table>
          @if (unpriced()) {
            <p class="muted">
              * Något i etappen saknar pris i katalogen och ingår inte i kostnaden.
            </p>
          }
        } @else {
          <p class="muted">Inga etapper än.</p>
        }
        @if (p.hiddenStages) {
          <p class="muted">{{ p.hiddenStages }} etapper ligger utanför ditt omfång.</p>
        }
        @if (canWrite()) {
          <form class="stage" (submit)="$event.preventDefault(); addStage()">
            <label
              >Ny etapp
              <input
                name="stage"
                placeholder="{{ p.name }}, etapp {{ p.stages.length + 1 }}"
                [value]="stageName()"
                (input)="stageName.set($any($event.target).value)"
                maxlength="200"
              />
            </label>
            <button type="submit" class="action" [disabled]="busy()">Lägg till etapp</button>
          </form>
        }
      </section>
      <section>
        <h3>Förslag på noder</h3>
        <form class="suggest" (submit)="$event.preventDefault(); suggest(p.id)">
          <label
            >Högst per nod
            <select [value]="capacity()" (change)="capacity.set(+$any($event.target).value)">
              <option value="64">64 enheter</option>
              <option value="128">128 enheter</option>
            </select>
          </label>
          <button type="submit" class="action">Föreslå noder</button>
        </form>
        @if (suggestions.hasValue()) {
          @let s = suggestions.value();
          <p class="muted">
            {{ s.nodes.length }} noder för {{ s.servicePoints }} anslutningspunkter med
            {{ s.units }} enheter ({{ s.elapsedMs }} ms).
            @if (!openStage()) {
              Lägg till en etapp för att lägga förslagen i en plan.
            }
          </p>
          @if (s.nodes.length) {
            <table class="rows">
              <thead>
                <tr>
                  <th>Nod</th>
                  <th class="num">Enheter</th>
                  <th class="num">Punkter</th>
                  <th class="num">Längst bort</th>
                  <th>Läge</th>
                  <th><span class="visually-hidden">Åtgärder</span></th>
                </tr>
              </thead>
              <tbody>
                @for (n of s.nodes; track n.number) {
                  <tr>
                    <td>{{ n.number }}</td>
                    <td class="num">{{ n.units }}{{ n.overCapacity ? ' (över taket)' : '' }}</td>
                    <td class="num">{{ n.servicePoints }}</td>
                    <td class="num">{{ n.reachM }} m</td>
                    <td>{{ n.snappedTo ? 'vid ' + n.snappedTo.code : 'rutnät' }}</td>
                    <td class="actions">
                      <button
                        type="button"
                        class="text-button"
                        (click)="map.focus({ x: n.x, y: n.y })"
                      >
                        Visa
                      </button>
                      @if (canWrite() && openStage(); as stage) {
                        @if (placed().has(n.number)) {
                          <span class="muted">I planen</span>
                        } @else {
                          <button
                            type="button"
                            class="text-button"
                            [disabled]="busy()"
                            (click)="place(p, stage, n)"
                          >
                            Lägg i planen
                          </button>
                        }
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          }
        } @else if (suggestions.error()) {
          <p class="error">Förslagen kunde inte räknas fram.</p>
        } @else if (suggestFor()) {
          <p class="loading">Räknar…</p>
        }
      </section>
    } @else if (project.error()) {
      <p class="error">Projektet kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      padding: var(--space-3) var(--space-4);
      overflow: auto;
      height: 100%;
      box-sizing: border-box;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    h2 {
      margin: 0;
      font-size: var(--text-lg);
    }
    h3 {
      margin: 0 0 var(--space-2);
      font-size: var(--text-md);
    }
    .close {
      border: 0;
      background: none;
      font-size: var(--text-xl);
      cursor: pointer;
      color: var(--text-muted);
    }
    .projects {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: var(--space-1);
    }
    .row {
      display: grid;
      gap: 2px;
      width: 100%;
      text-align: left;
      padding: var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-1);
      color: var(--text);
      cursor: pointer;
    }
    .name {
      font-weight: 600;
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .create,
    .stage {
      display: grid;
      gap: var(--space-2);
    }
    label {
      display: grid;
      gap: var(--space-1);
      font-size: var(--text-sm);
    }
    .buttons {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: center;
      margin: 0;
    }
    .primary {
      background: var(--action);
      color: var(--action-text);
      border: 0;
      font-weight: 600;
      justify-self: start;
    }
    .next {
      margin: var(--space-2) 0;
    }
    .hint {
      font-size: var(--text-sm);
      color: var(--text-muted);
    }
    .rows {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-sm);
    }
    .rows th,
    .rows td {
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
      text-align: left;
    }
    .rows .num {
      text-align: right;
      white-space: nowrap;
    }
    tfoot td {
      font-weight: 600;
    }
    .error {
      color: var(--status-conflict);
    }
    .suggest {
      display: flex;
      gap: var(--space-2);
      align-items: end;
      margin-bottom: var(--space-2);
    }
    .actions {
      white-space: nowrap;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
  `,
})
export class ProjectPanelComponent {
  protected readonly tools = inject(Tools);
  protected readonly map = inject(MapView);
  private readonly http = inject(HttpClient);
  private readonly plan = inject(ActivePlan);
  protected readonly canWrite = inject(CurrentUser).canWrite;

  private readonly revision = signal(0);
  protected readonly selected = signal<number | null>(null);
  protected readonly projects = httpResource<ProjectSummary[]>(() => {
    this.revision();
    return '/api/projects';
  });
  protected readonly project = httpResource<ProjectDetail>(() => {
    this.revision();
    const id = this.selected();
    return id === null ? undefined : `/api/projects/${id}`;
  });
  /** Node suggestions (#294): asked for by the button, for the open project and the chosen cap. */
  protected readonly capacity = signal(64);
  protected readonly suggestFor = signal<{ id: number; capacity: number } | null>(null);
  protected readonly suggestions = httpResource<NodeSuggestions>(() => {
    const f = this.suggestFor();
    return f ? `/api/projects/${f.id}/node-suggestions?capacity=${f.capacity}` : undefined;
  });
  /** Suggestions put in the plan, by number. */
  protected readonly placed = signal<ReadonlySet<number>>(new Set());
  /** The first stage still a draft: where suggestions go. */
  protected readonly openStage = computed(() =>
    this.project.hasValue()
      ? (this.project.value().stages.find((s) => s.plan.status === 'draft') ?? null)
      : null,
  );
  protected readonly newName = signal('');
  protected readonly newArea = signal<number[][] | null>(null);
  protected readonly stageName = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly unpriced = computed(
    () => this.project.hasValue() && this.project.value().stages.some((s) => s.unpriced),
  );

  protected readonly lifecycle = stageLifecycle;
  protected readonly statusText = stageStatusText;
  protected readonly cost = costText;

  /** Whether the next drawn area is the new project's or the open one's. */
  private drawingFor: 'new' | number | null = null;

  constructor() {
    // Suggested nodes marked in the map.
    effect(() => {
      if (!this.suggestions.hasValue()) {
        return;
      }
      const nodes = this.suggestions.value().nodes;
      untracked(() =>
        this.map.mark(
          nodes.map((n) => [-n.number, n.x, n.y] as const),
          null,
        ),
      );
    });
    // The open project's area in the map, while the tool is open.
    effect(() => {
      const p = this.project.hasValue() ? this.project.value() : null;
      this.map.areas.set(p?.area ? [{ id: p.id, name: p.name, rings: p.area }] : []);
    });
    effect(() => {
      const drawn = this.map.drawnArea();
      if (!drawn) {
        return;
      }
      untracked(() => {
        const target = this.drawingFor;
        this.drawingFor = null;
        if (target === 'new') {
          this.newArea.set(drawn.ring);
        } else if (target !== null) {
          void this.save(target, { area: drawn.ring });
        }
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.map.areas.set([]);
      this.map.drawingArea.set(false);
    });
  }

  protected select(id: number | null): void {
    this.selected.set(id);
    this.error.set(null);
    this.suggestFor.set(null);
    this.placed.set(new Set());
    this.map.clearMarks();
  }

  protected suggest(id: number): void {
    this.placed.set(new Set());
    this.suggestFor.set({ id, capacity: this.capacity() });
  }

  /** A suggested node as an access cabinet from the catalog's template, in the project's first open stage. */
  protected async place(p: ProjectDetail, stage: ProjectStage, n: NodeSuggestion): Promise<void> {
    const code = nodeCode(p.id, n.number);
    await this.run(async () => {
      await firstValueFrom(
        this.http.post(`/api/plans/${stage.plan.id}/templates`, {
          templateKey: 'skap-access',
          code,
          name: `${p.name}, nod ${n.number}`,
          x: n.x,
          y: n.y,
        }),
      );
      this.placed.update((s) => new Set([...s, n.number]));
    });
  }

  protected drawArea(): void {
    this.drawingFor = this.selected() ?? 'new';
    this.map.drawingArea.set(true);
  }

  protected showArea(p: ProjectDetail): void {
    if (p.extent) {
      this.map.mark([], p.extent);
    }
  }

  protected show(planId: number): void {
    this.plan.activate(planId);
  }

  protected async create(): Promise<void> {
    await this.run(async () => {
      const created = await firstValueFrom(
        this.http.post<ProjectDetail>('/api/projects', {
          name: this.newName().trim(),
          area: this.newArea(),
        }),
      );
      this.newName.set('');
      this.newArea.set(null);
      this.selected.set(created.id);
    });
  }

  protected async addStage(): Promise<void> {
    const id = this.selected();
    if (id === null) {
      return;
    }
    await this.run(async () => {
      await firstValueFrom(
        this.http.post<ProjectDetail>(`/api/projects/${id}/stages`, {
          name: this.stageName().trim(),
        }),
      );
      this.stageName.set('');
    });
  }

  private async save(id: number, change: { area?: number[][] }): Promise<void> {
    await this.run(async () => {
      await firstValueFrom(this.http.put<ProjectDetail>(`/api/projects/${id}`, change));
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
      this.revision.update((r) => r + 1);
    } catch (e: unknown) {
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.error.set(
        body?.detail ??
          (Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ||
            'Det gick inte.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
