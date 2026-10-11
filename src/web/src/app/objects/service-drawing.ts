import { HttpClient } from '@angular/common/http';
import {
  ApplicationRef,
  ChangeDetectionStrategy,
  Component,
  computed,
  createComponent,
  EnvironmentInjector,
  inject,
  Injectable,
  input,
  model,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PanelStack } from '../shell/panels';
import { CatalogImages } from './catalog-images';
import { fibreColour, metres } from './fibre-model';
import { ObjectRef } from './models';
import { ObjectLinkComponent } from './object-link';
import {
  conductorColourName,
  DrawingCable,
  DrawingEquipment,
  serviceLayout,
  ServiceDrawing,
  ViewStep,
} from './service-view-model';
import { layerLabels, terminalName } from './trace-model';

/**
 * The drawing in the service view (#257): one row per physical path, sites with their equipment pictures and the used
 * ports lit, cables between them with type, length and conductor colour, and bands for the circuits above. Kept apart
 * from the view so the performance panel can measure its frame time on its own.
 */
@Component({
  selector: 'cmdb-service-drawing',
  imports: [ObjectLinkComponent],
  template: `
    @for (row of layout().rows; track row.circuit.id; let first = $first; let i = $index) {
      @if (layout().rows.length > 1) {
        <h3 class="row-title">Väg {{ i + 1 }} <cmdb-link [ref]="row.circuit" /></h3>
      }
      <div class="grid" [style.grid-template-columns]="columns(row.steps)">
        @if (first) {
          @for (b of layout().bands; track b.circuit.id) {
            <button
              type="button"
              [class]="'band ' + b.layer"
              [style.grid-column]="b.from + ' / ' + b.to"
              [style.grid-row]="$index + 1"
              (click)="open(b.circuit)"
            >
              <span class="mono">{{ b.circuit.code }}</span>
              <span>{{ layers[b.layer] ?? b.layer }}</span>
            </button>
          }
        }
        @for (step of row.steps; track $index; let col = $index) {
          @if (step.type === 'site') {
            <div
              class="site"
              [class.splice]="!step.boxes.length"
              [style.grid-row]="first ? layout().bands.length + 1 : 1"
              [style.grid-column]="col + 1"
            >
              @if (step.site; as s) {
                <button type="button" class="site-name" (click)="open(s)">
                  <span class="mono">{{ s.code }}</span>
                  @if (s.name) {
                    <span class="muted">{{ s.name }}</span>
                  }
                </button>
              }
              @for (box of step.boxes; track $index) {
                @if (box.hidden) {
                  <div class="box hidden">Dold</div>
                } @else {
                  <button
                    type="button"
                    class="box"
                    [class.planned]="(box.equipment?.id ?? 0) < 0"
                    [class.chosen]="isChosen(box.equipment)"
                    (click)="choose(box.equipment)"
                  >
                    @if (equipment(box.equipment); as e) {
                      @if (e.image; as img) {
                        <span class="face" [style.aspect-ratio]="img.width + ' / ' + img.height">
                          @if (images.url(img.file)(); as src) {
                            <img [src]="src" alt="" />
                          }
                          @for (p of e.ports; track p.terminalId) {
                            @if (p.box; as pb) {
                              <span
                                class="lit"
                                [style.left.%]="(pb.x / img.width) * 100"
                                [style.top.%]="(pb.y / img.height) * 100"
                                [style.width.%]="(pb.width / img.width) * 100"
                                [style.height.%]="(pb.height / img.height) * 100"
                              ></span>
                            }
                          }
                        </span>
                      } @else {
                        <span class="face neutral">{{ e.model }}</span>
                      }
                    } @else {
                      <span class="face neutral">{{ box.equipment?.code ?? 'Utrustning' }}</span>
                    }
                    <span class="mono name">{{ box.equipment?.code }}</span>
                    <span class="ports">
                      @for (p of box.ports; track p.terminalId; let last = $last) {
                        {{ portName(p) }}{{ last ? '' : ' → ' }}
                      }
                    </span>
                  </button>
                }
              }
              @if (!step.boxes.length) {
                <span class="muted">skarv</span>
              }
            </div>
          } @else {
            <button
              type="button"
              class="span"
              [style.grid-row]="first ? layout().bands.length + 1 : 1"
              [style.grid-column]="col + 1"
              [class.planned]="step.cable.id < 0"
              [class.chosen]="isChosen(step.cable)"
              (click)="choose(step.cable)"
            >
              <span class="line" aria-hidden="true"></span>
              <span class="mono">{{ step.cable.code }}</span>
              @if (cable(step.cable); as c) {
                <span class="muted">{{ c.typeName }} · {{ metres(c.lengthM) }}</span>
              }
              @if (step.conductor) {
                <span class="conductor">
                  <span
                    class="swatch"
                    aria-hidden="true"
                    [style.background]="colour(step.conductor)"
                  ></span>
                  ledare {{ step.conductor }} ({{ colourName(step.conductor) }})
                </span>
              }
            </button>
          }
        }
      </div>
    } @empty {
      <p class="muted">Tjänsten har ingen fysisk väg som du kan se.</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .row-title {
      margin: var(--space-4) 0 var(--space-2);
      font-size: var(--text-sm);
      font-weight: 600;
    }
    .grid {
      display: grid;
      align-items: center;
      gap: var(--space-2) 0;
      min-width: max-content;
    }
    .band {
      display: flex;
      gap: var(--space-2);
      align-items: center;
      height: 22px;
      padding: 0 var(--space-2);
      border: 0;
      border-radius: var(--radius-sm);
      font: inherit;
      font-size: var(--text-xs);
      color: var(--text);
      cursor: pointer;
      background: color-mix(in srgb, var(--focus) 18%, transparent);
      &.logical {
        background: color-mix(in srgb, var(--status-planned) 22%, transparent);
      }
    }
    .site {
      display: flex;
      flex-direction: column;
      gap: var(--space-1);
      padding: var(--space-2);
      border: var(--line);
      border-radius: var(--radius-md);
      background: var(--surface-1);
      min-width: 150px;
      &.splice {
        min-width: 0;
        align-items: center;
        border-style: dashed;
      }
    }
    .site-name {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      padding: 0;
      border: 0;
      background: none;
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
      text-align: left;
    }
    .box {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding: var(--space-1);
      border: 1px solid transparent;
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      color: var(--text);
      font: inherit;
      font-size: var(--text-xs);
      text-align: left;
      cursor: pointer;
      &.planned {
        border: 1px dashed var(--status-planned);
      }
      &.chosen {
        border-color: var(--focus);
        box-shadow: 0 0 0 1px var(--focus);
      }
      &.hidden {
        color: var(--text-muted);
        cursor: default;
      }
    }
    .face {
      position: relative;
      display: block;
      width: 220px;
      max-width: 100%;
      img {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        opacity: 0.45;
      }
      &.neutral {
        width: auto;
        padding: var(--space-2);
        border: var(--line);
        border-radius: var(--radius-sm);
        color: var(--text-muted);
        background: var(--surface-1);
      }
    }
    .lit {
      position: absolute;
      border: 2px solid var(--focus);
      border-radius: 2px;
      background: color-mix(in srgb, var(--focus) 35%, transparent);
    }
    .name {
      font-size: var(--text-sm);
    }
    .ports {
      color: var(--text-muted);
    }
    .span {
      position: relative;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 2px;
      min-width: 120px;
      padding: var(--space-4) var(--space-2) var(--space-1);
      border: 0;
      background: none;
      color: var(--text);
      font: inherit;
      font-size: var(--text-xs);
      cursor: pointer;
      .line {
        position: absolute;
        left: 0;
        right: 0;
        top: 10px;
        border-top: 3px solid var(--border-strong);
      }
      &.planned .line {
        border-top-style: dashed;
        border-top-color: var(--status-planned);
      }
      &.chosen .line {
        border-top-color: var(--focus);
      }
    }
    .conductor {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .swatch {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
    }
    .muted {
      color: var(--text-muted);
    }
    .mono {
      font-family: var(--font-mono);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ServiceDrawingComponent {
  protected readonly images = inject(CatalogImages);
  private readonly panels = inject(PanelStack);

  readonly drawing = input.required<ServiceDrawing>();
  /** A cable or equipment chosen in the drawing, to see whether the service has another way. */
  readonly chosen = model<ObjectRef | null>(null);

  protected readonly layout = computed(() => serviceLayout(this.drawing().trace));
  private readonly equipmentById = computed(
    () => new Map(this.drawing().equipment.map((e) => [e.id, e])),
  );
  private readonly cableById = computed(() => new Map(this.drawing().cables.map((c) => [c.id, c])));

  protected readonly layers = layerLabels;
  protected readonly metres = metres;
  protected readonly portName = terminalName;

  /** Sites as wide as they need, cables sharing what is left. */
  protected columns(steps: readonly ViewStep[]): string {
    return steps.map((s) => (s.type === 'site' ? 'max-content' : 'minmax(120px, 1fr)')).join(' ');
  }

  protected equipment(ref: ObjectRef | null): DrawingEquipment | undefined {
    return ref ? this.equipmentById().get(ref.id) : undefined;
  }

  protected cable(ref: ObjectRef): DrawingCable | undefined {
    return this.cableById().get(ref.id);
  }

  protected colour(n: number): string {
    return fibreColour(conductorColourName(n));
  }

  protected colourName(n: number): string {
    return conductorColourName(n) ?? '';
  }

  protected isChosen(ref: ObjectRef | null): boolean {
    const c = this.chosen();
    return !!ref && !!c && c.type === ref.type && c.id === ref.id;
  }

  /** Opens the object and marks it, so the view says whether the service survives losing it. */
  protected choose(ref: ObjectRef | null): void {
    if (!ref) {
      return;
    }
    this.chosen.set(ref);
    this.open(ref);
  }

  protected open(ref: ObjectRef): void {
    if (ref.id > 0) {
      this.panels.open({ type: ref.type, id: String(ref.id) });
    }
  }
}

/** A frame-time measurement of the service view (#257): the service, its hops, and the time between frames. */
export interface ServiceBenchmark {
  service: string;
  hops: number;
  frames: number[];
}

/**
 * The service last shown in the service view, and a measurement of drawing it (#257): every frame another cable or
 * piece of equipment is chosen, so the whole drawing is checked again. It is drawn in an overlay of its own, since the
 * performance panel is a tool too and the service view closes when it opens.
 */
@Injectable({ providedIn: 'root' })
export class ServiceViews {
  private readonly http = inject(HttpClient);
  private readonly appRef = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);

  lastService: number | null = null;

  async measure(signal: AbortSignal): Promise<ServiceBenchmark | null> {
    if (this.lastService === null) {
      return null;
    }
    const drawing = await firstValueFrom(
      this.http.get<ServiceDrawing>(`/api/services/${this.lastService}/drawing`),
    );
    const hops = drawing.trace.circuits.reduce((n, c) => n + c.hops.length, 0);
    const targets: ObjectRef[] = [
      ...drawing.cables.map((c) => ({ type: 'cable' as const, id: c.id, code: String(c.id) })),
      ...drawing.equipment.map((e) => ({ type: 'equipment' as const, id: e.id, code: e.model })),
    ];
    const host = document.createElement('div');
    host.setAttribute('aria-hidden', 'true');
    host.style.cssText =
      'position:fixed;top:48px;left:0;right:0;bottom:0;z-index:50;overflow:hidden;background:var(--bg)';
    document.body.appendChild(host);
    const ref = createComponent(ServiceDrawingComponent, {
      environmentInjector: this.injector,
      hostElement: host,
    });
    ref.setInput('drawing', drawing);
    this.appRef.attachView(ref.hostView);
    const frames: number[] = [];
    try {
      let last = await nextFrame();
      for (let i = 0; i < 120; i++) {
        signal.throwIfAborted();
        ref.setInput('chosen', targets.length ? targets[i % targets.length] : null);
        ref.changeDetectorRef.detectChanges();
        const t = await nextFrame();
        frames.push(t - last);
        last = t;
      }
    } finally {
      this.appRef.detachView(ref.hostView);
      ref.destroy();
      host.remove();
    }
    return { service: drawing.trace.service?.code ?? String(this.lastService), hops, frames };
  }
}

const nextFrame = () => new Promise<number>((resolve) => requestAnimationFrame(resolve));
