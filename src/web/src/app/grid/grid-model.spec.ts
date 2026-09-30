import {
  cellValue,
  editColumns,
  fillDown,
  GridRow,
  paste,
  parseTsv,
  setCell,
  toOperations,
} from './grid-model';

const row = (id: number, attributes: Record<string, unknown> = {}): GridRow => ({
  type: 'equipment',
  id,
  code: `E${id}`,
  name: `Switch ${id}`,
  lifecycle: 'in_service',
  site: { id: 1, code: 'S1' },
  typeKey: 'acme-ax-24',
  model: 'Acme AX-24',
  attributes,
});

const columns = editColumns({
  columns: [
    { key: 'firmware', type: 'string', values: null },
    { key: 'ports', type: 'number', values: null },
  ],
});
const [name, lifecycle, firmware, ports] = columns;

describe('grid model', () => {
  it('reads spreadsheet text by line and tab, without the trailing newline', () => {
    expect(parseTsv('a\tb\r\nc\td\n')).toEqual([
      ['a', 'b'],
      ['c', 'd'],
    ]);
    expect(parseTsv('x')).toEqual([['x']]);
  });

  it('keeps an edit only while it differs, and types numbers', () => {
    const r = row(1, { firmware: '1.0' });
    let edits = setCell(new Map(), r, firmware, '2.0');
    expect(cellValue(r, firmware, edits)).toBe('2.0');
    edits = setCell(edits, r, firmware, '1.0');
    expect(edits.size).toBe(0);
    edits = setCell(edits, r, ports, '4,5');
    expect(edits.get(1)!.attributes!['ports']).toBe(4.5);
    edits = setCell(edits, r, firmware, '');
    expect(edits.get(1)!.attributes!['firmware']).toBeNull();
    // Unknown lifecycles are ignored rather than sent.
    expect(setCell(new Map(), r, lifecycle, 'burning').size).toBe(0);
  });

  it('pastes a block from the active cell to the right and down, within the grid', () => {
    const rows = [row(1), row(2)];
    const edits = paste(
      new Map(),
      rows,
      columns,
      { row: 0, column: 2 },
      '3.1\t8\n3.2\t16\n3.3\t32\n',
    );

    expect(rows.map((r) => cellValue(r, firmware, edits))).toEqual(['3.1', '3.2']);
    expect(rows.map((r) => cellValue(r, ports, edits))).toEqual(['8', '16']);
  });

  it('fills the top value down the range, whichever way it was marked', () => {
    const rows = [row(1, { firmware: '9' }), row(2), row(3), row(4)];
    const edits = fillDown(new Map(), rows, firmware, 2, 0);

    expect(rows.map((r) => cellValue(r, firmware, edits))).toEqual(['9', '9', '9', '']);
  });

  it('turns edits into plan operations, row by row', () => {
    const rows = [row(1), row(2, { firmware: '1' })];
    let edits = setCell(new Map(), rows[0], name, 'Ny switch');
    edits = setCell(edits, rows[0], lifecycle, 'decommissioning');
    edits = setCell(edits, rows[1], firmware, '');

    expect(toOperations(rows, edits)).toEqual([
      { row: 0, operation: { kind: 'rename', type: 'equipment', objectId: 1, name: 'Ny switch' } },
      {
        row: 0,
        operation: {
          kind: 'set_lifecycle',
          type: 'equipment',
          objectId: 1,
          lifecycle: 'decommissioning',
        },
      },
      {
        row: 1,
        operation: {
          kind: 'set_attributes',
          type: 'equipment',
          objectId: 2,
          attributes: { firmware: null },
        },
      },
    ]);
  });
});
