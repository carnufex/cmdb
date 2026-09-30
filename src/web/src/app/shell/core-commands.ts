import { inject } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { traceId } from '../objects/trace-model';
import { CommandRegistry } from './commands';
import { PanelStack } from './panels';
import { ThemeStore } from './theme';
import { Tools } from './tools';

/** The shell's own commands. Call once from the root component's injection context. */
export function registerCoreCommands(): void {
  const registry = inject(CommandRegistry);
  const panels = inject(PanelStack);
  const tools = inject(Tools);
  const theme = inject(ThemeStore);
  const document = inject(DOCUMENT);

  registry.register(
    {
      id: 'trace.service',
      label: 'Spåra tjänst ände till ände',
      keywords: ['trace', 'väg', 'kretsschema'],
      contextual: true,
      when: (c) => c.top?.type === 'service',
      run: (c) =>
        panels.open({ type: 'trace', id: traceId({ by: 'service', id: Number(c.top!.id) }) }),
    },
    {
      id: 'trace.circuit',
      label: 'Spåra kretsen',
      keywords: ['trace', 'väg', 'lager'],
      contextual: true,
      when: (c) => c.top?.type === 'circuit',
      run: (c) =>
        panels.open({ type: 'trace', id: traceId({ by: 'circuit', id: Number(c.top!.id) }) }),
    },
    {
      id: 'tools.query',
      label: 'Avancerad sökning',
      hint: 'Verktyg',
      keywords: ['hitta', 'filter', 'fråga', 'siter'],
      run: () => {
        if (tools.open() !== 'query') {
          tools.toggle('query');
        }
      },
    },
    {
      id: 'tools.tree',
      label: 'Visa innehållsträd',
      hint: 'Verktyg',
      keywords: ['träd', 'rack', 'portar', 'kort'],
      contextual: true,
      when: (c) => c.top?.type === 'site' || c.top?.type === 'equipment',
      run: () => {
        if (tools.open() !== 'tree') {
          tools.toggle('tree');
        }
      },
    },
    {
      id: 'tools.plans',
      label: 'Planer',
      hint: 'Verktyg',
      keywords: ['plan', 'projekt', 'etapp', 'ändringsmängd', 'produktion'],
      run: () => {
        if (tools.open() !== 'plans') {
          tools.toggle('plans');
        }
      },
    },
    {
      id: 'tools.grid',
      label: 'Kalkylark',
      hint: 'Verktyg',
      keywords: ['excel', 'massredigering', 'attribut', 'lasso', 'urval'],
      run: () => {
        if (tools.open() !== 'grid') {
          tools.toggle('grid');
        }
      },
    },
    {
      id: 'tools.perf',
      label: 'Prestandamätning',
      hint: 'Verktyg',
      keywords: ['p95', 'budget', 'benchmark'],
      run: () => {
        if (tools.open() !== 'perf') {
          tools.toggle('perf');
        }
      },
    },
    {
      id: 'theme.toggle',
      label: 'Byt till ljust eller mörkt tema',
      keywords: ['tema', 'ljus', 'mörk', 'projektor'],
      run: () => theme.toggle(),
    },
    {
      id: 'panels.back',
      label: 'Tillbaka i panelstacken',
      keywords: ['föregående', 'back'],
      when: (c) => c.panels.length > 1,
      run: (c) => panels.truncate(c.panels.length - 2),
    },
    {
      id: 'panels.close',
      label: 'Stäng panelerna',
      hint: 'Esc',
      when: (c) => c.panels.length > 0,
      run: () => panels.close(),
    },
    {
      id: 'view.copy-link',
      label: 'Kopiera länk till vyn',
      keywords: ['dela', 'url'],
      when: (c) => c.panels.length > 0,
      run: () => navigator.clipboard?.writeText(document.location.href),
    },
  );
}
