import { HttpClient, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Selection } from '../grid/selection';
import { MapView } from '../map/map-view';
import { CatalogKinds } from '../shell/catalog-kinds';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { Tools } from '../shell/tools';
import {
  EquipmentDraft,
  emptyEquipment,
  examplesFor,
  numericOps,
  Op,
  opLabels,
  QueryDraft,
  QueryFields,
  serviceTypeLabels,
  SiteQueryResult,
  textOps,
  toRequest,
} from './query-model';

/**
 * Advanced search (#55): sites by their own fields, the equipment they hold and the services passing through.
 * Results open in the panel stack and are marked in the map.
 */
@Component({
  selector: 'cmdb-query-panel',
  imports: [StatusComponent],
  templateUrl: './query-panel.html',
  styleUrl: './query-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QueryPanelComponent {
  private readonly http = inject(HttpClient);
  private readonly mapView = inject(MapView);
  private readonly panels = inject(PanelStack);
  private readonly selection = inject(Selection);
  protected readonly tools = inject(Tools);

  protected readonly fields = httpResource<QueryFields>(() => '/api/query/fields');
  protected readonly draft = signal<QueryDraft>({
    siteTypes: [],
    lifecycles: [],
    serviceTypes: [],
    equipment: [emptyEquipment()],
  });
  protected readonly result = signal<SiteQueryResult | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly running = signal(false);

  protected readonly examples = computed(() => examplesFor(this.fields.value()));
  protected readonly opLabels = opLabels;
  protected readonly kinds = inject(CatalogKinds);
  protected readonly serviceTypeLabels = serviceTypeLabels;

  protected readonly categories = computed(() => this.fields.value()?.categories ?? []);

  protected typesFor(category: string) {
    const types = this.fields.value()?.types ?? [];
    return category ? types.filter((t) => t.category === category) : types;
  }

  protected attributesFor(e: EquipmentDraft) {
    const category =
      e.category || this.fields.value()?.types.find((t) => t.key === e.typeKey)?.category;
    const cats = this.categories();
    const attributes = category
      ? (cats.find((c) => c.key === category)?.attributes ?? [])
      : cats.flatMap((c) => c.attributes);
    return [...new Map(attributes.map((a) => [a.key, a])).values()];
  }

  protected opsFor(e: EquipmentDraft): Op[] {
    const attribute = this.attributesFor(e).find((a) => a.key === e.key);
    return attribute?.type === 'number' ? numericOps : textOps;
  }

  protected valuesFor(e: EquipmentDraft) {
    return this.attributesFor(e).find((a) => a.key === e.key)?.values ?? null;
  }

  protected toggle(list: 'siteTypes' | 'lifecycles' | 'serviceTypes', value: string): void {
    this.draft.update((d) => ({
      ...d,
      [list]: d[list].includes(value) ? d[list].filter((v) => v !== value) : [...d[list], value],
    }));
  }

  protected updateEquipment(i: number, change: Partial<EquipmentDraft>): void {
    this.draft.update((d) => ({
      ...d,
      equipment: d.equipment.map((e, j) => {
        if (j !== i) {
          return e;
        }
        const next = { ...e, ...change };
        // A new category or model can make the attribute meaningless; start the attribute over.
        if ('category' in change && change.category !== e.category) {
          next.typeKey = '';
          next.key = '';
          next.value = '';
        }
        if ('key' in change) {
          next.op = 'eq';
          next.value = '';
        }
        return next;
      }),
    }));
  }

  protected addEquipment(): void {
    this.draft.update((d) => ({ ...d, equipment: [...d.equipment, emptyEquipment()] }));
  }

  protected removeEquipment(i: number): void {
    this.draft.update((d) => ({ ...d, equipment: d.equipment.filter((_, j) => j !== i) }));
  }

  protected useExample(draft: QueryDraft): void {
    this.draft.set(structuredClone(draft));
    void this.run();
  }

  protected reset(): void {
    this.draft.set({
      siteTypes: [],
      lifecycles: [],
      serviceTypes: [],
      equipment: [emptyEquipment()],
    });
    this.result.set(null);
    this.error.set(null);
    this.mapView.clearMarks();
  }

  async run(): Promise<void> {
    this.running.set(true);
    this.error.set(null);
    try {
      const result = await firstValueFrom(
        this.http.post<SiteQueryResult>(
          '/api/query/sites',
          toRequest(this.draft(), this.fields.value()),
        ),
      );
      this.result.set(result);
      this.mapView.mark(result.points, result.extent);
    } catch (e: unknown) {
      const errors = (e as { error?: { errors?: Record<string, string[]> } }).error?.errors;
      this.error.set(errors ? Object.values(errors).flat().join(' ') : 'Sökningen misslyckades.');
    } finally {
      this.running.set(false);
    }
  }

  protected open(site: SiteQueryResult['sites'][number]): void {
    this.panels.open({ type: 'site', id: String(site.id) }, { replace: true });
    if (site.x !== null && site.y !== null) {
      this.mapView.focus({ x: site.x, y: site.y });
    }
  }

  /** The matches as a spreadsheet (#27): the grid opens with them selected. */
  protected openGrid(result: SiteQueryResult): void {
    this.selection.sites.set({
      ids: result.points.map((p) => p[0]),
      label: `${result.points.length} siter från avancerad sökning`,
    });
    this.tools.toggle('grid');
  }

  protected close(): void {
    this.mapView.clearMarks();
    this.tools.close();
  }
}
