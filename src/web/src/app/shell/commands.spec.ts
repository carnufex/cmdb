import { describe, expect, it } from 'vitest';
import { Command, CommandContext, fold, matchCommands } from './commands';

const noop = () => undefined;
const commands: Command[] = [
  { id: 'a', label: 'Avancerad sökning', keywords: ['filter'], run: noop },
  {
    id: 't',
    label: 'Spåra tjänst ände till ände',
    keywords: ['trace'],
    contextual: true,
    when: (c) => c.top?.type === 'service',
    run: noop,
  },
  { id: 'b', label: 'Byt tema', run: noop },
];
const none: CommandContext = { top: null, panels: [] };
const service: CommandContext = {
  top: { type: 'service', id: '1' },
  panels: [{ type: 'service', id: '1' }],
};

describe('matchCommands', () => {
  it('lists what applies, contextual first', () => {
    expect(matchCommands(commands, '', none).map((c) => c.id)).toEqual(['a', 'b']);
    expect(matchCommands(commands, '', service).map((c) => c.id)).toEqual(['t', 'a', 'b']);
  });

  it('matches every word, ignoring case and Swedish letters, in label and keywords', () => {
    expect(matchCommands(commands, 'spara tj', service).map((c) => c.id)).toEqual(['t']);
    expect(matchCommands(commands, 'TRACE', service).map((c) => c.id)).toEqual(['t']);
    expect(matchCommands(commands, 'filter', none).map((c) => c.id)).toEqual(['a']);
    expect(matchCommands(commands, 'spåra', none)).toEqual([]);
  });

  it('folds å, ä and ö', () => {
    expect(fold('Spåra ÄNDE ö')).toBe('spara ande o');
  });
});
