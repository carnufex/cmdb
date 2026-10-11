import { historyChangeText, historyCommandText, historyKindText } from './history';

describe('object history', () => {
  it('names changes in words', () => {
    expect(historyChangeText({ field: 'name', before: 'Gammal', after: 'Ny' })).toBe(
      'Namn: Gammal → Ny',
    );
    expect(historyChangeText({ field: 'lifecycle', before: 'planned', after: 'in_service' })).toBe(
      'Status: Planerad → I drift',
    );
    expect(historyChangeText({ field: 'attributes.höjd', before: null, after: '12' })).toBe(
      'höjd: – → 12',
    );
    expect(historyChangeText({ field: 'geom', before: null, after: null })).toBe('Läge ändrat');
    expect(historyChangeText({ field: 'site_type', before: 'a', after: 'b' })).toBe(
      'Sitetyp: a → b',
    );
  });

  it('names kinds and commands', () => {
    expect(historyKindText('changed')).toBe('Ändrad');
    expect(historyCommandText('apply_plan')).toBe('införd plan');
    expect(historyCommandText('something_new')).toBe('something_new');
  });
});
