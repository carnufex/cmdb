import { ScrollingModule, CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { HttpClient, httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { portStatusLabels } from '../objects/front-panel-model';
import { EquipmentDetail, SiteDetail } from '../objects/models';
import { traceId } from '../objects/trace-model';
import { CatalogKinds } from '../shell/catalog-kinds';
import { IconComponent, locationIcon } from '../shell/icons';
import { PanelStack } from '../shell/panels';
import { Tools } from '../shell/tools';
import {
  equipmentChildren,
  find,
  flatten,
  groupChildren,
  navigate,
  networkTree,
  pathTo,
  rangeFor,
  siteTree,
  TreeGroupContent,
  TreeNode,
  TreeRoot,
  TreeRow,
} from './tree-model';
import { ToolSizeComponent } from '../shell/tool-size';

const ROW_HEIGHT = 26;

/**
 * The content tree (#17, #250): the whole network inside the caller's scopes, from the country down to the port:
 * site types, ranges of codes for large types, sites, locations, equipment, cards and ports, each level fetched when it
 * is expanded. Rows are virtualised (Angular CDK), so the whole network stays smooth; the tree follows the panel stack,
 * opening the way to the open object, and keyboard navigation follows the WAI-ARIA tree view.
 */
@Component({
  selector: 'cmdb-tree-panel',
  imports: [ToolSizeComponent, ScrollingModule, IconComponent],
  template: `
    <header class="head">
      <h2>Innehåll</h2>
      <cmdb-tool-size />
      <button
        type="button"
        class="close"
        aria-label="Stäng innehållsträdet"
        (click)="tools.close()"
      >
        ×
      </button>
    </header>
    @if (root(); as r) {
      <div class="bar">
        <span class="muted">{{ rows().length }} rader</span>
        <button
          type="button"
          class="action"
          [disabled]="loading() > 0 || !openSite()"
          [title]="openSite() ? 'Expandera allt i ' + openSite()!.label : 'Öppna en site först'"
          (click)="expandAll()"
        >
          Expandera siten
        </button>
        <button type="button" class="action" (click)="collapseAll()">Fäll ihop</button>
        @if (loading() > 0) {
          <span class="muted">hämtar {{ loading() }}…</span>
        }
      </div>
      <cdk-virtual-scroll-viewport
        [itemSize]="rowHeight"
        class="viewport"
        role="tree"
        [attr.aria-label]="'Innehåll i ' + r.label"
        tabindex="0"
        [attr.aria-activedescendant]="'tree-' + active()"
        (keydown)="onKey($event)"
      >
        <div
          *cdkVirtualFor="let row of rows(); let i = index; trackBy: track"
          class="row"
          role="treeitem"
          [id]="'tree-' + i"
          [attr.aria-level]="row.depth + 1"
          [attr.aria-expanded]="row.expandable ? row.expanded : null"
          [attr.aria-selected]="i === active()"
          [attr.aria-label]="ariaLabel(row.node)"
          [class.active]="i === active()"
          [class.current]="row.node.key === currentKey()"
          [style.padding-left.px]="8 + row.depth * 14"
          (click)="active.set(i); open(row)"
        >
          <button
            type="button"
            class="twisty"
            tabindex="-1"
            [class.hidden]="!row.expandable"
            [attr.aria-label]="row.expanded ? 'Fäll ihop' : 'Expandera'"
            (click)="$event.stopPropagation(); active.set(i); toggle(row)"
          >
            {{ row.expanded ? '▾' : '▸' }}
          </button>
          <cmdb-icon class="icon" [name]="icon(row.node)" [title]="kinds[row.node.kind]" />
          @if (row.node.portStatus; as s) {
            <span [class]="'dot ' + s" [title]="labels[s]"></span>
          }
          <span class="label mono">{{ row.node.label }}</span>
          @if (row.node.detail) {
            <span class="detail">{{ row.node.detail }}</span>
          }
        </div>
      </cdk-virtual-scroll-viewport>
    } @else if (network.error()) {
      <p class="empty">Nätet gick inte att hämta.</p>
    } @else {
      <p class="empty">Hämtar…</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      min-height: 36px;
      padding: 0 var(--space-2) 0 var(--space-4);
      border-bottom: var(--line);
      h2 {
        margin: 0;
        font-size: var(--text-md);
        font-weight: 600;
      }
    }
    .close {
      width: 26px;
      height: 26px;
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .bar {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-3);
      border-bottom: var(--line);
      font-size: var(--text-sm);
    }
    .action {
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      cursor: pointer;
      &:disabled {
        color: var(--text-muted);
      }
    }
    .viewport {
      flex: 1;
      min-height: 0;
      outline: none;
      &:focus-visible .row.active {
        outline: 2px solid var(--focus);
        outline-offset: -2px;
      }
    }
    .row {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      height: 26px;
      padding-right: var(--space-3);
      font-size: var(--text-sm);
      white-space: nowrap;
      cursor: pointer;
      &:hover {
        background: var(--surface-2);
      }
      &.active {
        background: var(--surface-2);
      }
      &.current .label {
        color: var(--focus);
        font-weight: 600;
      }
    }
    .twisty {
      width: 16px;
      padding: 0;
      border: 0;
      background: none;
      color: var(--text-muted);
      cursor: pointer;
      &.hidden {
        visibility: hidden;
      }
    }
    .icon {
      color: var(--text-muted);
    }
    .detail,
    .muted {
      color: var(--text-muted);
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .dot {
      width: 8px;
      height: 8px;
      margin: 0 2px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
      &.connected {
        background: var(--status-in-service);
        border-color: transparent;
      }
      &.planned {
        background: var(--status-planned);
        border-color: transparent;
      }
      &.conflict {
        background: var(--status-conflict);
        border-color: transparent;
      }
    }
    .empty {
      padding: var(--space-4);
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TreePanelComponent {
  private readonly http = inject(HttpClient);
  private readonly panels = inject(PanelStack);
  protected readonly tools = inject(Tools);
  private readonly viewport = viewChild(CdkVirtualScrollViewport);

  protected readonly rowHeight = ROW_HEIGHT;
  protected readonly labels = portStatusLabels;
  private readonly catalog = inject(CatalogKinds);
  protected readonly kinds: Record<TreeNode['kind'], string> = {
    network: 'Nätet',
    group: 'Sitetyp',
    range: 'Siter',
    site: 'Site',
    location: 'Plats',
    equipment: 'Utrustning',
    port: 'Port',
  };

  /** The row's icon (#249): the network, the site type's, the location kind's, the category's or a card's. */
  protected icon(node: TreeNode): string {
    switch (node.kind) {
      case 'network':
        return 'network';
      case 'group':
      case 'range':
      case 'site':
        return this.catalog.siteIcon(node.typeKey);
      case 'location':
        return locationIcon(node.typeKey ?? '');
      case 'equipment':
        return node.typeKey === 'card' ? 'card' : this.catalog.categoryIcon(node.typeKey);
      default:
        return 'port';
    }
  }

  /** What the row is, in words, since its icon is only decoration. */
  protected ariaLabel(node: TreeNode): string {
    const type =
      node.kind === 'equipment' && node.typeKey === 'card'
        ? 'Kort'
        : node.kind === 'site'
          ? this.catalog.siteTypeName(node.typeKey ?? '')
          : node.kind === 'equipment'
            ? this.catalog.categoryName(node.typeKey ?? '')
            : node.kind === 'port'
              ? 'Port'
              : '';
    const status = node.portStatus ? this.labels[node.portStatus] : null;
    return [type, node.label, node.detail, status].filter(Boolean).join(', ');
  }

  /** The whole network inside the caller's scopes is the root (#250); each level loads when it is expanded. */
  protected readonly network = httpResource<TreeRoot>(() => '/api/tree');

  /** Equipment on top of the stack tells us its site; other panels keep the last site. */
  private readonly topEquipment = httpResource<EquipmentDetail>(() => {
    const top = this.panels.top();
    return top?.type === 'equipment' ? `/api/equipment/${top.id}` : undefined;
  });

  protected readonly siteId = signal<number | null>(null);
  private readonly site = httpResource<SiteDetail>(() => {
    const id = this.siteId();
    return id ? `/api/sites/${id}` : undefined;
  });

  protected readonly root = signal<TreeNode | null>(null);
  private readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Bumped when lazily loaded children arrive. */
  private readonly version = signal(0);
  protected readonly loading = signal(0);
  protected readonly active = signal(0);

  protected readonly rows = computed<TreeRow[]>(() => {
    this.version();
    const root = this.root();
    return root ? flatten(root, this.expanded()) : [];
  });

  /** The object open in the panel stack, as a tree key. */
  protected readonly currentKey = computed(() => {
    const top = this.panels.top();
    return top ? `${top.type}:${top.id}` : null;
  });

  /** The open site's node, the part "Expandera siten" works on: never the whole network. */
  protected readonly openSite = computed(() => {
    this.version();
    const id = this.siteId();
    const root = this.root();
    return id !== null && root ? find(root, `site:${id}`) : null;
  });

  constructor() {
    effect(() => {
      const network = this.network.value();
      untracked(() => {
        if (network && !this.root()) {
          const root = networkTree(network);
          this.root.set(root);
          this.expanded.set(new Set([root.key]));
          this.active.set(0);
        }
      });
    });
    effect(() => {
      const top = this.panels.top();
      const equipment = this.topEquipment.value();
      untracked(() => {
        if (top?.type === 'site') {
          this.siteId.set(Number(top.id));
        } else if (top?.type === 'equipment' && equipment && String(equipment.id) === top.id) {
          this.siteId.set(equipment.site.id);
        }
      });
    });
    // Follow the panel stack: open the way from the root to the site, then reveal the open object.
    effect(() => {
      const site = this.site.value();
      const key = this.currentKey();
      const root = this.root();
      untracked(() => {
        if (site && root) {
          void this.revealSite(site).then(() => (key ? this.reveal(key) : undefined));
        }
      });
    });
  }

  protected track = (_: number, row: TreeRow) => row.node.key;

  protected async toggle(row: TreeRow): Promise<void> {
    if (row.expanded) {
      this.expanded.update((s) => {
        const next = new Set(s);
        next.delete(row.node.key);
        return next;
      });
      return;
    }
    await this.load(row.node);
    this.expanded.update((s) => new Set(s).add(row.node.key));
  }

  /** Fetches a node's children the first time it is expanded: a site type, a range, a site or equipment. */
  private async load(node: TreeNode): Promise<void> {
    if (!node.lazy) {
      return;
    }
    this.loading.update((n) => n + 1);
    try {
      node.children = await this.children(node);
      node.lazy = false;
      this.version.update((v) => v + 1);
    } finally {
      this.loading.update((n) => n - 1);
    }
  }

  private async children(node: TreeNode): Promise<TreeNode[]> {
    const type = encodeURIComponent(node.typeKey ?? '');
    switch (node.kind) {
      case 'group':
        return groupChildren(
          node.typeKey!,
          await firstValueFrom(this.http.get<TreeGroupContent>(`/api/tree/${type}`)),
        );
      case 'range': {
        const params = { from: node.range!.from, to: node.range!.to };
        const content = await firstValueFrom(
          this.http.get<TreeGroupContent>(`/api/tree/${type}`, { params }),
        );
        return groupChildren(node.typeKey!, content);
      }
      case 'site':
        return siteTree(
          await firstValueFrom(this.http.get<SiteDetail>(`/api/sites/${node.ref!.id}`)),
        ).children;
      case 'equipment':
        return equipmentChildren(
          await firstValueFrom(this.http.get<EquipmentDetail>(`/api/equipment/${node.ref!.id}`)),
        );
      default:
        return node.children;
    }
  }

  protected open(row: TreeRow): void {
    const node = row.node;
    if (node.kind === 'port' && node.terminalId && node.portStatus !== 'free') {
      this.panels.open({ type: 'trace', id: traceId({ by: 'terminal', id: node.terminalId }) });
    } else if (node.ref) {
      this.panels.open({ type: node.ref.type, id: String(node.ref.id) });
    } else {
      void this.toggle(row);
    }
  }

  protected onKey(event: KeyboardEvent): void {
    const rows = this.rows();
    if (event.key === 'Enter') {
      event.preventDefault();
      const row = rows[this.active()];
      if (row) {
        this.open(row);
      }
      return;
    }
    const move = navigate(rows, this.active(), event.key);
    if (!move) {
      return;
    }
    event.preventDefault();
    this.active.set(move.index);
    if (move.toggle) {
      void this.toggle(rows[move.index]);
    }
    this.viewport()?.scrollToIndex(Math.max(0, move.index - 3));
  }

  /** Expands every location and equipment of the open site, loading ports and cards eight at a time. */
  protected async expandAll(): Promise<void> {
    const site = this.openSite();
    if (!site) {
      return;
    }
    const keys = new Set(this.expanded());
    const queue: TreeNode[] = [site];
    while (queue.length) {
      const batch = queue.splice(0, 8);
      await Promise.all(batch.map((n) => this.load(n)));
      for (const node of batch) {
        if (node.children.length) {
          keys.add(node.key);
          queue.push(...node.children.filter((c) => c.kind !== 'port'));
        }
      }
      this.expanded.set(new Set(keys));
    }
  }

  protected collapseAll(): void {
    const root = this.root();
    this.expanded.set(new Set(root ? [root.key] : []));
    this.active.set(0);
  }

  /** Loads the way from the root to the site: its type, the range holding its code, and the site itself. */
  private async revealSite(site: SiteDetail): Promise<void> {
    const root = this.root();
    const group = root ? find(root, `group:${site.siteType}`) : null;
    if (!root || !group) {
      return;
    }
    await this.load(group);
    const range = rangeFor(group, site.code);
    if (range) {
      await this.load(range);
    }
    const node = find(range ?? group, `site:${site.id}`);
    if (!node) {
      return;
    }
    if (node.lazy) {
      node.children = siteTree(site).children;
      node.lazy = false;
      this.version.update((v) => v + 1);
    }
    const path = pathTo(root, node.key)!;
    this.expanded.update((s) => {
      const next = new Set(s);
      path.forEach((k) => next.add(k));
      // The site's own rooms and racks, as when it was the root.
      node.children.forEach((c) => next.add(c.key));
      return next;
    });
  }

  private async reveal(key: string): Promise<void> {
    const root = this.root();
    if (!root || !find(root, key)) {
      return;
    }
    const path = pathTo(root, key)!;
    this.expanded.update((s) => {
      const next = new Set(s);
      path.slice(0, -1).forEach((k) => next.add(k));
      return next;
    });
    const index = this.rows().findIndex((r) => r.node.key === key);
    if (index >= 0) {
      this.active.set(index);
      // After the virtual list has taken the new rows, or it scrolls within the old ones.
      setTimeout(() => {
        const viewport = this.viewport();
        viewport?.checkViewportSize();
        viewport?.scrollToIndex(Math.max(0, index - 3));
      });
    }
  }
}
