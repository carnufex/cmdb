import { ScrollingModule } from '@angular/cdk/scrolling';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { SiteQueryHit } from '../query/query-model';
import { Lifecycle, StatusComponent } from '../shell/status';
import { Assistant, SiteList } from './assistant';

const ROW = 32;
const VISIBLE_ROWS = 9;

/** Every site a question gave, from our own API: a list to scroll and filter, whose rows open the site and go there on the map. */
@Component({
  selector: 'cmdb-assistant-sites',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ScrollingModule, StatusComponent],
  template: `
    <p class="sum">
      {{ headline() }}
    </p>
    @if (list().rows.length > 8) {
      <input
        #filter
        type="search"
        placeholder="Filtrera på kod eller namn"
        aria-label="Filtrera siterna"
        [value]="query()"
        (input)="query.set(filter.value)"
      />
    }
    @if (rows().length > 0) {
      <cdk-virtual-scroll-viewport
        [itemSize]="rowHeight"
        [style.height.px]="height()"
        class="viewport"
      >
        <button
          *cdkVirtualFor="let site of rows(); trackBy: byId"
          type="button"
          class="row"
          (click)="open(site)"
        >
          <span class="code">{{ site.code }}</span>
          <span class="name">{{ site.name }}</span>
          <cmdb-status [value]="lifecycle(site)" />
        </button>
      </cdk-virtual-scroll-viewport>
    } @else if (query()) {
      <p class="sum">Inget matchar filtret.</p>
    }
    @if (list().total > list().rows.length) {
      <p class="sum">Listan visar de {{ count(list().rows.length) }} första.</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
    }
    .sum {
      margin: 0;
      font-size: var(--text-sm);
      color: var(--text-muted);
    }
    input {
      width: 100%;
      font: inherit;
      font-size: var(--text-sm);
    }
    .viewport {
      border: var(--line);
      border-radius: var(--radius-sm);
    }
    .row {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      height: 32px;
      padding: 0 var(--space-2);
      border: 0;
      border-bottom: var(--line);
      background: none;
      color: inherit;
      font: inherit;
      font-size: var(--text-sm);
      text-align: left;
      cursor: pointer;
    }
    .row:hover {
      background: var(--surface-2);
    }
    .code {
      flex: none;
      font-family: var(--font-mono);
      font-size: var(--text-xs);
    }
    .name {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      color: var(--text-muted);
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
})
export class AssistantSitesComponent {
  private readonly assistant = inject(Assistant);
  readonly list = input.required<SiteList>();
  protected readonly rowHeight = ROW;
  protected readonly query = signal('');

  protected readonly rows = computed(() => {
    const query = this.query().trim().toLowerCase();
    return query
      ? this.list().rows.filter(
          (s) => s.code.toLowerCase().includes(query) || s.name.toLowerCase().includes(query),
        )
      : this.list().rows;
  });
  protected readonly height = computed(() => Math.min(this.rows().length, VISIBLE_ROWS) * ROW + 2);

  protected readonly headline = computed(() => {
    const { total, marked, positionsHidden } = this.list();
    const sites = total === 1 ? '1 site' : `${this.count(total)} siter`;
    if (positionsHidden) {
      return `${sites}. Din behörighet döljer var de ligger.`;
    }
    if (marked === null) {
      return sites;
    }
    if (total === 0) {
      return 'Inga siter att markera på kartan.';
    }
    return total > marked
      ? `${this.count(marked)} av ${sites} markerade på kartan.`
      : `${sites} markerade på kartan.`;
  });

  protected count(n: number): string {
    return n.toLocaleString('sv-SE');
  }

  protected lifecycle(site: SiteQueryHit): Lifecycle {
    return site.lifecycle as Lifecycle;
  }

  protected readonly byId = (_: number, site: SiteQueryHit) => site.id;

  protected open(site: SiteQueryHit): void {
    this.assistant.openObject(
      `site:${site.id}`,
      site.x !== null && site.y !== null ? { x: site.x, y: site.y } : undefined,
    );
  }
}
