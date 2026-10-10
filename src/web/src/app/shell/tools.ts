import { computed, inject, Injectable } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';

/** Tools opened from the toolbar. They sit over the left edge of the lens, never on top of the panel stack. */
export type Tool = 'query' | 'perf' | 'tree' | 'plans' | 'grid' | 'changelog' | 'voice' | 'rack';

/**
 * How much room a tool takes (#251): a panel over the map, half the screen beside it, or the whole workspace under
 * the toolbar with the map hidden (it keeps its view).
 */
export type ToolSize = 'panel' | 'half' | 'workspace';

export interface ToolSpec {
  /** The sizes the tool works in, smallest first. */
  sizes: readonly ToolSize[];
  /** The size it opens in until the user picks another. */
  initial: ToolSize;
}

export const toolSpecs: Record<Tool, ToolSpec> = {
  query: { sizes: ['panel', 'half'], initial: 'panel' },
  tree: { sizes: ['panel', 'half'], initial: 'panel' },
  plans: { sizes: ['panel', 'half', 'workspace'], initial: 'half' },
  grid: { sizes: ['half', 'workspace'], initial: 'half' },
  changelog: { sizes: ['panel', 'half'], initial: 'panel' },
  voice: { sizes: ['panel'], initial: 'panel' },
  perf: { sizes: ['panel', 'half', 'workspace'], initial: 'panel' },
  rack: { sizes: ['half', 'workspace'], initial: 'workspace' },
};

/** What a tool shows, in the address beside it: the rack view's rack (#255). Cleared when the tool closes. */
const toolParams: Record<string, readonly string[]> = { rack: ['rack'] };

export const toolSizeNames: Record<ToolSize, string> = {
  panel: 'Panel',
  half: 'Halva skärmen',
  workspace: 'Arbetsyta',
};

const tools = Object.keys(toolSpecs) as Tool[];
const sizes: readonly ToolSize[] = ['panel', 'half', 'workspace'];

const storageKey = (tool: Tool) => `cmdb.tool-size.${tool}`;

/** The size a tool opens in: the one last picked for it on this device, or its own. */
export function preferredSize(tool: Tool, storage: Pick<Storage, 'getItem'> | null): ToolSize {
  let stored: string | null = null;
  try {
    stored = storage?.getItem(storageKey(tool)) ?? null;
  } catch {
    stored = null;
  }
  const spec = toolSpecs[tool];
  return spec.sizes.includes(stored as ToolSize) ? (stored as ToolSize) : spec.initial;
}

/** The next size in the tool's list, round again after the largest. */
export function nextSize(tool: Tool, size: ToolSize): ToolSize {
  const list = toolSpecs[tool].sizes;
  return list[(list.indexOf(size) + 1) % list.length];
}

/**
 * The open tool and its size live in the address (?tool=plans&size=half), like the panel stack, so a link opens the
 * same view and back and forward move through it. The size picked last is kept per tool on this device.
 */
@Injectable({ providedIn: 'root' })
export class Tools {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly params = toSignal(
    this.route.queryParamMap.pipe(map((q) => ({ tool: q.get('tool'), size: q.get('size') }))),
    { initialValue: { tool: null, size: null } },
  );

  readonly open = computed<Tool | null>(() => {
    const tool = this.params().tool;
    return tools.includes(tool as Tool) ? (tool as Tool) : null;
  });

  readonly size = computed<ToolSize>(() => {
    const tool = this.open();
    if (!tool) {
      return 'panel';
    }
    const size = this.params().size as ToolSize;
    return toolSpecs[tool].sizes.includes(size) ? size : preferredSize(tool, storage());
  });

  /** The sizes the open tool can switch between. */
  readonly sizes = computed(() => {
    const tool = this.open();
    return tool ? toolSpecs[tool].sizes : [];
  });

  toggle(tool: Tool): void {
    if (this.open() === tool) {
      this.close();
    } else {
      this.navigate(tool, preferredSize(tool, storage()));
    }
  }

  /** Opens a tool on something, such as the rack view on a rack (#255): `show('rack', { rack: '12' })`. */
  show(tool: Tool, params: Record<string, string>): void {
    this.navigate(
      tool,
      this.open() === tool ? this.size() : preferredSize(tool, storage()),
      params,
    );
  }

  close(): void {
    this.navigate(null, null);
  }

  /** Switches the open tool to a size it supports and remembers it for the tool. */
  setSize(size: ToolSize): void {
    const tool = this.open();
    if (!tool || !toolSpecs[tool].sizes.includes(size) || !sizes.includes(size)) {
      return;
    }
    try {
      storage()?.setItem(storageKey(tool), size);
    } catch {
      // Private windows and blocked storage: the size still holds for this view.
    }
    this.navigate(tool, size);
  }

  /** The keyboard shortcut (Alt+.): the next size the open tool supports. */
  cycleSize(): void {
    const tool = this.open();
    if (tool) {
      this.setSize(nextSize(tool, this.size()));
    }
  }

  private navigate(
    tool: Tool | null,
    size: ToolSize | null,
    params: Record<string, string> = {},
  ): void {
    // Another tool's parameters go with it.
    const cleared = Object.fromEntries(
      Object.entries(toolParams)
        .filter(([owner]) => owner !== tool)
        .flatMap(([, names]) => names.map((n) => [n, null])),
    );
    void this.router.navigate([], {
      queryParams: { ...cleared, ...params, tool, size: tool && size !== 'panel' ? size : null },
      queryParamsHandling: 'merge',
    });
  }
}

function storage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}
