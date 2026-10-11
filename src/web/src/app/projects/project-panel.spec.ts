import {
  costText,
  distributionTypes,
  nodeCode,
  ProjectStage,
  stageLifecycle,
  stageStatusText,
} from './project-panel';

const stage = (status: string, problems = 0, flag: string | null = null): ProjectStage => ({
  seq: 1,
  plan: { id: 1, name: 'Etapp 1', status, flag },
  changes: 3,
  problems,
  cableMetres: 1200,
  cost: 1234567.4,
  unpriced: false,
});

describe('project stages', () => {
  it('show their state as a dot and text', () => {
    expect([stageLifecycle(stage('applied')), stageStatusText(stage('applied'))]).toEqual([
      'in_service',
      'Införd',
    ]);
    expect([stageLifecycle(stage('cancelled')), stageStatusText(stage('cancelled'))]).toEqual([
      'removed',
      'Avbruten',
    ]);
    expect([stageLifecycle(stage('draft', 2)), stageStatusText(stage('draft', 2))]).toEqual([
      'conflict',
      '2 passar inte',
    ]);
    expect([
      stageLifecycle(stage('draft', 0, 'ändrad')),
      stageStatusText(stage('draft', 0, 'ändrad')),
    ]).toEqual(['conflict', 'Flaggad']);
    expect([stageLifecycle(stage('draft')), stageStatusText(stage('draft'))]).toEqual([
      'planned',
      'Utkast',
    ]);
  });

  it('give costs in whole units with Swedish thousands', () => {
    expect(costText(1234567.4, 'SEK').replace(/\s/g, ' ')).toBe('1 234 567 SEK');
  });
});

describe('node suggestions', () => {
  it('give a suggested node a site code from the project and its number', () => {
    expect(nodeCode(12, 3)).toBe('P12-N03');
    expect(nodeCode(4, 14)).toBe('P4-N14');
  });
});

describe('distribution', () => {
  it('sums the cables by type, largest first', () => {
    const cable = (typeKey: string, typeName: string, conductors: number, lengthM: number) => ({
      from: 0,
      to: 1,
      units: 1,
      fibres: 1,
      typeKey,
      typeName,
      conductors,
      lengthM,
      inConduitM: 0,
    });
    expect(
      distributionTypes({
        points: [],
        cables: [
          cable('fiber-12', 'Fiber 12', 12, 300),
          cable('fiber-96', 'Fiber 96', 96, 1200),
          cable('fiber-12', 'Fiber 12', 12, 200),
        ],
        lengthM: 1700,
        units: 0,
        warnings: [],
        operations: 0,
        elapsedMs: 1,
      }),
    ).toEqual([
      { typeName: 'Fiber 96', count: 1, metres: 1200 },
      { typeName: 'Fiber 12', count: 2, metres: 500 },
    ]);
  });
});
