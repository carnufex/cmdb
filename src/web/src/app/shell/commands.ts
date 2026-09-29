import { computed, inject, Injectable, signal } from '@angular/core';
import { ObjectRef, PanelStack } from './panels';

/** What a command can see: the object on top of the panel stack, and the whole stack. */
export interface CommandContext {
  top: ObjectRef | null;
  panels: readonly ObjectRef[];
}

/**
 * An action in the command palette (#21). Registered by the shell or by a panel for as long as its context
 * lasts; `when` decides whether it applies right now.
 */
export interface Command {
  id: string;
  label: string;
  /** Shown to the right, e.g. a shortcut or what it acts on. */
  hint?: string;
  /** Extra words to match, e.g. "trace" for "Spåra". */
  keywords?: readonly string[];
  /** Commands about the open object come first. */
  contextual?: boolean;
  when?: (ctx: CommandContext) => boolean;
  run: (ctx: CommandContext) => void | Promise<void>;
}

/** Folds case and Swedish letters so "spara" finds "Spåra". */
export function fold(text: string): string {
  return text.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '');
}

/**
 * The commands that apply in the context and match the query: every word of the query must start a word in the
 * label or keywords, or appear in them. Contextual commands first, then by label.
 */
export function matchCommands(
  commands: readonly Command[],
  query: string,
  ctx: CommandContext,
): Command[] {
  const words = fold(query).split(/\s+/).filter(Boolean);
  return commands
    .filter((c) => c.when?.(ctx) ?? true)
    .filter((c) => {
      const text = fold([c.label, c.hint ?? '', ...(c.keywords ?? [])].join(' '));
      return words.every((w) => text.includes(w));
    })
    .sort(
      (a, b) =>
        Number(b.contextual ?? false) - Number(a.contextual ?? false) ||
        a.label.localeCompare(b.label, 'sv'),
    );
}

@Injectable({ providedIn: 'root' })
export class CommandRegistry {
  private readonly stack = inject(PanelStack);
  private readonly commands = signal<readonly Command[]>([]);

  readonly context = computed<CommandContext>(() => ({
    top: this.stack.top(),
    panels: this.stack.panels(),
  }));

  readonly all = this.commands.asReadonly();

  /** Adds commands (replacing any with the same id) and returns a function that removes them again. */
  register(...commands: Command[]): () => void {
    const ids = new Set(commands.map((c) => c.id));
    this.commands.update((list) => [...list.filter((c) => !ids.has(c.id)), ...commands]);
    return () =>
      this.commands.update((list) => list.filter((c) => !ids.has(c.id) || !commands.includes(c)));
  }

  match(query: string): Command[] {
    return matchCommands(this.commands(), query, this.context());
  }

  async run(command: Command): Promise<void> {
    await command.run(this.context());
  }
}
