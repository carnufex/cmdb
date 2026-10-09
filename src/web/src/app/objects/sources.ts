import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ObjectSource, SourceValue } from './models';

const attributeLabels: Record<string, string> = {
  code: 'Kod',
  name: 'Namn',
  siteType: 'Sitetyp',
  type: 'Typ',
  kind: 'Sort',
  layer: 'Lager',
  lifecycle: 'Status',
  rackUnits: 'Höjdenheter',
  position: 'Position',
  placement: 'Placering',
  ends: 'Ändar',
  route: 'Sträckning',
};

const attributePrefix = 'attributes.';

/** The attribute's label: Swedish for the recorded ones, the key for the object's own. */
export function sourceAttributeLabel(attribute: string): string {
  return attribute.startsWith(attributePrefix)
    ? attribute.slice(attributePrefix.length)
    : (attributeLabels[attribute] ?? attribute);
}

/** The value as the source reported it; positions in whole metres (SWEREF 99 TM). */
export function sourceValueText(value: SourceValue): string {
  const v = value.value;
  if (v === null || v === undefined) {
    return '';
  }
  if (value.attribute === 'position' && Array.isArray(v)) {
    return v.map((c) => Math.round(Number(c))).join(', ');
  }
  if (Array.isArray(v)) {
    return v.join(', ');
  }
  return typeof v === 'object' ? JSON.stringify(v) : String(v);
}

/**
 * The sources that reported an object (#215, ADR-0019): when each last confirmed it, what it said per attribute,
 * which source owns the attribute by the catalog's priority, and whether the object has changed since. A change is
 * shown as dot and text, never colour alone.
 */
@Component({
  selector: 'cmdb-sources',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (list().length) {
      <section>
        <h3>Källor</h3>
        @for (s of list(); track s.source) {
          <details class="source">
            <summary>
              <span class="mono">{{ s.source }}</span>
              <span class="muted">{{ s.externalId }}</span>
              <span class="muted">bekräftad {{ confirmed(s) }}</span>
              @if (s.origin) {
                <span class="muted">skapade objektet</span>
              }
              @if (changed(s); as n) {
                <span class="changed"
                  ><span class="dot" aria-hidden="true"></span>{{ n }} ändrade sedan dess</span
                >
              }
            </summary>
            <table class="rows">
              <thead>
                <tr>
                  <th>Attribut</th>
                  <th>Enligt källan</th>
                  <th>Läge</th>
                </tr>
              </thead>
              <tbody>
                @for (v of s.values; track v.attribute) {
                  <tr>
                    <td>{{ label(v.attribute) }}</td>
                    <td class="mono">{{ text(v) }}</td>
                    <td>
                      @if (!v.current) {
                        <span class="changed"
                          ><span class="dot" aria-hidden="true"></span>Ändrad i cmdb</span
                        >
                      } @else {
                        <span class="muted">Som i källan</span>
                      }
                      @if (v.owner) {
                        <span class="owner">· äger</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </details>
        }
      </section>
    }
  `,
  styleUrls: ['./panel.scss', './sources.scss'],
})
export class SourcesComponent {
  readonly sources = input<ObjectSource[] | undefined>();

  protected readonly list = computed(() => this.sources() ?? []);

  protected readonly label = sourceAttributeLabel;
  protected readonly text = sourceValueText;

  private readonly changedCounts = computed(
    () => new Map(this.list().map((s) => [s.source, s.values.filter((v) => !v.current).length])),
  );

  protected changed(s: ObjectSource): number {
    return this.changedCounts().get(s.source) ?? 0;
  }

  protected confirmed(s: ObjectSource): string {
    return new Date(s.confirmedAt).toLocaleString('sv-SE', {
      dateStyle: 'short',
      timeStyle: 'short',
    });
  }
}
