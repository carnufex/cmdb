import { afterNextRender, DestroyRef, Directive, ElementRef, inject } from '@angular/core';

/** The narrowest the search box may be and still be used (#332, #352). */
export const SEARCH_MIN = 200;

/** What the toolbar looks like after layout: how far its contents overflow, and how wide the search box is. */
export interface ToolbarLayout {
  /** Pixels that a control or a group of controls lies outside the room it has (0 when everything fits). */
  overflow: number;
  /** The search box's width. */
  search: number;
}

/** Whether the toolbar fits as drawn: nothing pushed out of its place, and a search box that can be used. */
export function fits(layout: ToolbarLayout, searchMin = SEARCH_MIN): boolean {
  return layout.overflow <= 0.5 && layout.search >= searchMin;
}

/**
 * Measures the toolbar as it is drawn now; reading sizes forces a layout, so a class just changed is included. Each
 * control must lie inside its group (`.command`, `.actions`) and each group inside the bar. Boxes, not scroll widths:
 * a popover the tools open (Tidsresa) is positioned outside the flow and must not count as overflow.
 */
export function measure(bar: HTMLElement): ToolbarLayout {
  const room = bar.getBoundingClientRect();
  let overflow = 0;
  for (const group of Array.from(bar.querySelectorAll<HTMLElement>('.command, .actions'))) {
    const box = group.getBoundingClientRect();
    overflow = Math.max(overflow, room.left - box.left, box.right - room.right);
    for (const control of Array.from(group.children)) {
      const r = control.getBoundingClientRect();
      if (r.width === 0 && r.height === 0) continue;
      overflow = Math.max(overflow, box.left - r.left, r.right - box.right);
    }
  }
  const search =
    bar.querySelector('cmdb-search .search')?.getBoundingClientRect().width ??
    Number.POSITIVE_INFINITY;
  return { overflow, search };
}

/**
 * The toolbar shows its tools with their names when they fit and as icons when they do not (#352). Under 1440 px they
 * are always icons (styles.scss, #332); above, what fits depends on the person's name, the chat button and the active
 * plan, so the toolbar measures itself: on every change of its size or contents it is laid out with names, and if that
 * does not fit — a control is pushed out of its place, or the search box is under 200 px — it gets the class `compact`
 * before the browser paints, so nothing flickers.
 */
@Directive({ selector: '[cmdbToolbarFit]' })
export class ToolbarFitDirective {
  private readonly bar = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;

  constructor() {
    const destroy = inject(DestroyRef);
    afterNextRender(() => {
      // Without the observers (the unit tests' DOM) the toolbar keeps its names, and the media rule still applies.
      if (typeof ResizeObserver === 'undefined' || typeof MutationObserver === 'undefined') return;
      const fit = (): void => {
        this.bar.classList.remove('compact');
        if (!fits(measure(this.bar))) this.bar.classList.add('compact');
      };
      // The toolbar's own size follows the window; its contents change when the name, a plan or the chat arrive. The
      // class it sets is an attribute, which the mutation observer does not watch, so fitting never feeds itself.
      const resize = new ResizeObserver(fit);
      resize.observe(this.bar);
      const mutation = new MutationObserver(fit);
      mutation.observe(this.bar, { childList: true, subtree: true, characterData: true });
      fit();
      destroy.onDestroy(() => {
        resize.disconnect();
        mutation.disconnect();
      });
    });
  }
}
