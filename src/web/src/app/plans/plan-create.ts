import { HttpClient, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { MapView } from '../map/map-view';
import { ActivePlan } from './active-plan';
import { resolveSite, SiteChoice } from './plan-model';

interface Fields {
  siteTypes: string[];
  types: { key: string; manufacturer: string; model: string; category: string }[];
  cableTypes: { key: string; name: string; medium: string; conductors: number }[] | null;
}

type Tab = 'site' | 'equipment' | 'cable';

/**
 * Creating objects in the active plan (#107): a site at the map's centre, equipment at a site, and a cable between two
 * sites. Sites are given by code, existing or planned in the plan; planned ones become violet in the map.
 */
@Component({
  selector: 'cmdb-plan-create',
  imports: [FormsModule],
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
    @switch (tab()) {
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
          <p class="muted">Placeras vid kartans mittpunkt.</p>
          <button type="submit" class="action" [disabled]="busy() || !code.trim() || !name.trim()">
            Lägg till site
          </button>
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
      }
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
    .tabs {
      display: flex;
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
export class PlanCreateComponent {
  private readonly http = inject(HttpClient);
  private readonly mapView = inject(MapView);
  private readonly active = inject(ActivePlan);

  protected readonly tabs: { key: Tab; label: string }[] = [
    { key: 'site', label: 'Ny site' },
    { key: 'equipment', label: 'Ny utrustning' },
    { key: 'cable', label: 'Ny kabel' },
  ];
  protected readonly tab = signal<Tab>('site');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly fields = httpResource<Fields>(() => '/api/query/fields');
  protected readonly siteTypes = computed(() => this.fields.value()?.siteTypes ?? ['radio']);
  protected readonly models = computed(() =>
    (this.fields.value()?.types ?? []).filter((t) => t.category !== 'card'),
  );
  protected readonly cableTypes = computed(() => this.fields.value()?.cableTypes ?? []);
  protected readonly plannedSites = computed<SiteChoice[]>(
    () => this.active.view.value()?.planned?.sites ?? [],
  );

  protected code = '';
  protected name = '';
  protected siteType = 'radio';
  protected site = '';
  protected typeKey = '';
  protected siteA = '';
  protected siteB = '';
  protected cableType = '';

  protected async createSite(): Promise<void> {
    const center = this.mapView.center();
    if (!center) {
      this.error.set('Kartan har ingen mittpunkt än. Öppna kartan först.');
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
    });
  }

  protected async createEquipment(): Promise<void> {
    await this.run(async () => {
      const siteId = await this.site_(this.site);
      await this.active.add({
        kind: 'create_equipment',
        siteId,
        typeKey: this.typeKey,
        name: this.name.trim(),
      });
      this.name = '';
    });
  }

  protected async createCable(): Promise<void> {
    await this.run(async () => {
      const [aSiteId, bSiteId] = [await this.site_(this.siteA), await this.site_(this.siteB)];
      await this.active.add({ kind: 'create_cable', aSiteId, bSiteId, typeKey: this.cableType });
    });
  }

  /** A site by code: planned in the plan, or an exact hit in the quick search. */
  private async site_(code: string): Promise<number> {
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
