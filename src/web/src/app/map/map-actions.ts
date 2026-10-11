import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Selection } from '../grid/selection';
import { fibreTarget } from '../objects/fibre-model';
import { SiteDetail } from '../objects/models';
import { ActivePlan } from '../plans/active-plan';
import { PlanDraft } from '../plans/plan-draft';
import { Command, CommandContext, CommandRegistry } from '../shell/commands';
import { PanelStack } from '../shell/panels';
import { Tools } from '../shell/tools';
import { MapTarget, MapView, Point } from './map-view';

/** One thing the context menu (#258) or the command palette can do with what is under the pointer. */
export interface MapAction {
  id: string;
  label: string;
  /** Changes the network: it goes into the active plan, and only someone who may write sees it. */
  changes?: boolean;
  /** Needs the point clicked (in the palette, which has none, it is left out). */
  needsPoint?: boolean;
  run: (target: MapTarget | null, point: Point | null) => Promise<string | void> | string | void;
}

/**
 * What can be done where one looks in the map (#258): on an empty spot, a site, a cable or a route segment. The same
 * actions run from the command palette for the open object (#21). Changes go into the active plan through the plan
 * panel's forms, which check them as always; the server checks every write.
 */
@Injectable({ providedIn: 'root' })
export class MapActions {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly panels = inject(PanelStack);
  private readonly tools = inject(Tools);
  private readonly draft = inject(PlanDraft);
  private readonly selection = inject(Selection);
  private readonly mapView = inject(MapView);
  private readonly plan = inject(ActivePlan);
  private readonly commands = inject(CommandRegistry);

  readonly empty: MapAction[] = [
    {
      id: 'map.new-site',
      label: 'Ny site här',
      changes: true,
      needsPoint: true,
      run: (_, point) => {
        this.mapView.place(point!);
        this.draft.tab.set('template');
        this.openPlans();
      },
    },
    {
      id: 'map.new-cable',
      label: 'Ny kabel härifrån',
      changes: true,
      run: () => {
        this.draft.tab.set('cable');
        this.openPlans();
        this.mapView.picking.set(true);
      },
    },
    {
      id: 'map.measure',
      label: 'Mät avstånd',
      needsPoint: true,
      run: (_, point) => {
        this.mapView.measuring.set(point);
      },
    },
    {
      id: 'map.copy',
      label: 'Kopiera koordinat',
      needsPoint: true,
      run: async (_, point) => {
        const text = `${Math.round(point!.x)} ${Math.round(point!.y)}`;
        await navigator.clipboard.writeText(text);
        return `Kopierat: ${text} (SWEREF 99 TM)`;
      },
    },
  ];

  readonly site: MapAction[] = [
    { id: 'site.open', label: 'Öppna', run: (t) => this.open(t!) },
    {
      id: 'site.contents',
      label: 'Innehåll',
      run: (t) => {
        this.open(t!);
        if (this.tools.open() !== 'tree') {
          this.tools.toggle('tree');
        }
      },
    },
    {
      id: 'site.rack',
      label: 'Rackvy',
      run: async (t) => {
        const site = await firstValueFrom(this.http.get<SiteDetail>(`/api/sites/${t!.id}`));
        const rack = site.locations.find((l) => l.kind === 'rack');
        if (!rack) {
          return 'Siten har inget rack.';
        }
        this.tools.show('rack', { rack: String(rack.id) });
        return undefined;
      },
    },
    {
      id: 'site.new-cable',
      label: 'Ny kabel härifrån',
      changes: true,
      run: (t) => {
        this.draft.cableFrom.set(t!.code);
        this.openPlans();
      },
    },
    {
      id: 'site.impact',
      label: 'Påverkan om siten faller',
      run: (t) => {
        this.selection.pick({ siteIds: [t!.id], cableIds: [] });
        this.selection.impactRequest.update((n) => n + 1);
      },
    },
    {
      id: 'site.neighbourhood',
      label: 'Grannskap',
      run: (t) => {
        void this.router.navigate(['/graf'], {
          queryParams: { p: `site:${t!.id}` },
          queryParamsHandling: 'merge',
        });
      },
    },
    { id: 'site.select', label: 'Lägg till i urvalet', run: (t) => this.select(t!) },
  ];

  readonly cable: MapAction[] = [
    { id: 'cable.open', label: 'Öppna', run: (t) => this.open(t!) },
    {
      id: 'cable.insert-site',
      label: 'Sätt in en site här',
      changes: true,
      needsPoint: true,
      run: async (t, point) => {
        // Where the point projects onto the cable (#169), as a share of its length.
        const near = await firstValueFrom(
          this.http.get<{ cable: { id: number }; at: number }[]>(
            `/api/cables/near?x=${Math.round(point!.x)}&y=${Math.round(point!.y)}&radius=2000`,
          ),
        );
        const hit = near.find((n) => n.cable.id === t!.id);
        this.open(t!);
        this.draft.split.set({ cableId: t!.id, at: hit?.at ?? 0.5 });
      },
    },
    {
      id: 'cable.break',
      label: 'Kabelbrott här',
      needsPoint: true,
      run: (t, point) => {
        this.tools.show('fibre', {
          fibre: fibreTarget('cables', t!.id),
          at: `${Math.round(point!.x)},${Math.round(point!.y)}`,
        });
      },
    },
    {
      id: 'cable.fibre',
      label: 'Fiberritning',
      run: (t) => this.tools.show('fibre', { fibre: fibreTarget('cables', t!.id) }),
    },
    {
      id: 'cable.reserve',
      label: 'Reservera fiber',
      changes: true,
      run: (t) => this.open(t!),
    },
    { id: 'cable.select', label: 'Lägg till i urvalet', run: (t) => this.select(t!) },
  ];

  readonly segment: MapAction[] = [
    { id: 'segment.open', label: 'Öppna', run: (t) => this.open(t!) },
    { id: 'segment.dig', label: 'Påverkan vid grävning', run: (t) => this.open(t!) },
    {
      id: 'segment.fibre',
      label: 'Fiberritning',
      run: (t) => this.tools.show('fibre', { fibre: fibreTarget('route-segments', t!.id) }),
    },
  ];

  /**
   * The same actions in the command palette (#21) for the open site, cable or route segment, except those that need a
   * point in the map and "Öppna", which it already is. Returns the unregistration.
   */
  registerCommands(canWrite: () => boolean): () => void {
    const typeNames = { site: 'Site', cable: 'Kabel', 'route-segment': 'Trasésträcka' } as const;
    const commands: Command[] = (['site', 'cable', 'route-segment'] as const).flatMap((type) =>
      this.for({ type, id: 0, code: '' })
        .filter((a) => !a.needsPoint && !a.id.endsWith('.open'))
        .map((a) => ({
          id: `map.${a.id}`,
          label: a.label,
          hint: typeNames[type],
          contextual: true,
          when: (c: CommandContext) => c.top?.type === type && (!a.changes || canWrite()),
          run: async (c: CommandContext) => {
            const top = c.top!;
            await a.run(
              { type, id: Number(top.id), code: this.panels.label(top) ?? `#${top.id}` },
              null,
            );
          },
        })),
    );
    return this.commands.register(...commands);
  }

  /** The actions for what is under the pointer, or for an empty spot. */
  for(target: MapTarget | null): MapAction[] {
    if (!target) {
      return this.empty;
    }
    return target.type === 'site' ? this.site : target.type === 'cable' ? this.cable : this.segment;
  }

  /** Whether the plan panel must create a plan first: a change, and no plan active. */
  needsPlan(action: MapAction): boolean {
    return !!action.changes && this.plan.id() === null;
  }

  private open(t: MapTarget): void {
    this.panels.open({ type: t.type, id: String(t.id) }, { replace: true });
  }

  private select(t: MapTarget): void {
    this.selection.pick(
      { siteIds: t.type === 'site' ? [t.id] : [], cableIds: t.type === 'cable' ? [t.id] : [] },
      'add',
    );
  }

  private openPlans(): void {
    if (this.tools.open() !== 'plans') {
      this.tools.toggle('plans');
    }
  }
}

/** A distance as one reads it: metres up to a kilometre, then kilometres with one decimal. */
export function distanceText(metres: number): string {
  return metres < 1000
    ? `${Math.round(metres)} m`
    : `${(metres / 1000).toLocaleString('sv-SE', { maximumFractionDigits: 1, minimumFractionDigits: 1 })} km`;
}
