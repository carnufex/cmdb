import { importText } from '../plans/plan-create';
import { servicePointLifecycle, servicePointStatusText } from './service-point-panel';

describe('service points', () => {
  it('show their status as a dot and text', () => {
    expect([servicePointLifecycle('planned'), servicePointStatusText('planned')]).toEqual([
      'planned',
      'Planerad',
    ]);
    expect([servicePointLifecycle('ordered'), servicePointStatusText('ordered')]).toEqual([
      'under_construction',
      'Beställd',
    ]);
    expect([servicePointLifecycle('connected'), servicePointStatusText('connected')]).toEqual([
      'in_service',
      'Ansluten',
    ]);
  });

  it('say what an import adds', () => {
    expect(
      importText({
        dryRun: true,
        servicePoints: 3,
        units: 26,
        skipped: 0,
        errors: [],
        elapsedMs: 1,
      }),
    ).toBe('Skulle lägga till 3 anslutningspunkter med 26 enheter');
    expect(
      importText({
        dryRun: false,
        sites: 2,
        equipment: 4,
        connections: 1,
        cables: 3,
        skipped: 0,
        errors: [],
        elapsedMs: 1,
      }),
    ).toBe('Lade till 2 siter, 4 utrustningar, 1 kopplingar och 3 kablar');
  });
});
