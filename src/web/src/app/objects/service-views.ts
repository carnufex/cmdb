import { HttpClient } from '@angular/common/http';
import {
  ApplicationRef,
  createComponent,
  EnvironmentInjector,
  inject,
  Injectable,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ObjectRef } from './models';
import { ServiceDrawing } from './service-view-model';

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
    // Loaded only when measured: the drawing stays out of the first bundle.
    const { ServiceDrawingComponent } = await import('./service-drawing');
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
