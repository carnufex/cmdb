import { NewOperation } from '../plans/plan-model';

/** POST /api/grid (#27) */
export interface GridColumn {
  key: string;
  type: 'string' | 'number' | 'boolean' | string;
  values: (string | number)[] | null;
}

export interface GridRow {
  type: 'site' | 'equipment';
  id: number;
  code: string;
  name: string;
  lifecycle: string;
  site: { id: number; code: string; name?: string | null } | null;
  typeKey: string | null;
  model: string | null;
  attributes: Record<string, unknown>;
}

export interface GridResult {
  kind: 'site' | 'equipment';
  columns: GridColumn[];
  rows: GridRow[];
  truncated: boolean;
}

/** An editable column: the name, the lifecycle, or an attribute. */
export interface EditColumn {
  key: string;
  label: string;
  kind: 'name' | 'lifecycle' | 'attribute';
  type: string;
  values: (string | number)[] | null;
}

/** A row's changes: a new name or lifecycle, and attribute values where null removes the key. */
export interface RowEdit {
  name?: string;
  lifecycle?: string;
  attributes?: Record<string, string | number | null>;
}

export type Edits = ReadonlyMap<number, RowEdit>;

export const lifecycles = [
  'planned',
  'under_construction',
  'in_service',
  'decommissioning',
  'removed',
];

export function editColumns(grid: Pick<GridResult, 'columns'>): EditColumn[] {
  return [
    { key: 'name', label: 'Namn', kind: 'name', type: 'string', values: null },
    { key: 'lifecycle', label: 'Livscykel', kind: 'lifecycle', type: 'string', values: lifecycles },
    ...grid.columns.map((c) => ({
      key: c.key,
      label: c.key,
      kind: 'attribute' as const,
      type: c.type,
      values: c.values,
    })),
  ];
}

/** The value a cell shows: the edit when there is one, otherwise the row's own. */
export function cellValue(row: GridRow, column: EditColumn, edits: Edits): string {
  const edit = edits.get(row.id);
  if (column.kind === 'name') {
    return edit?.name ?? row.name;
  }
  if (column.kind === 'lifecycle') {
    return edit?.lifecycle ?? row.lifecycle;
  }
  const changed =
    edit?.attributes && column.key in edit.attributes ? edit.attributes[column.key] : undefined;
  const value = changed !== undefined ? changed : row.attributes[column.key];
  return value === null || value === undefined ? '' : String(value);
}

/**
 * Sets a cell. Text is read by the column's type (numbers as numbers); an empty attribute removes it. Setting a cell
 * back to the row's own value drops the edit.
 */
export function setCell(
  edits: Edits,
  row: GridRow,
  column: EditColumn,
  text: string,
): Map<number, RowEdit> {
  const next = new Map(edits);
  const edit: RowEdit = { ...next.get(row.id), attributes: { ...next.get(row.id)?.attributes } };
  const value = text.trim();
  if (column.kind === 'name') {
    if (value === '' || value === row.name) {
      delete edit.name;
    } else {
      edit.name = value;
    }
  } else if (column.kind === 'lifecycle') {
    if (value === row.lifecycle || !lifecycles.includes(value)) {
      delete edit.lifecycle;
    } else {
      edit.lifecycle = value;
    }
  } else {
    const typed: string | number | null =
      value === ''
        ? null
        : column.type === 'number' && !Number.isNaN(Number(value.replace(',', '.')))
          ? Number(value.replace(',', '.'))
          : value;
    const original = row.attributes[column.key] ?? null;
    if (typed === original || (typed === null && !(column.key in row.attributes))) {
      delete edit.attributes![column.key];
    } else {
      edit.attributes![column.key] = typed;
    }
  }
  if (!Object.keys(edit.attributes!).length) {
    delete edit.attributes;
  }
  if (Object.keys(edit).length) {
    next.set(row.id, edit);
  } else {
    next.delete(row.id);
  }
  return next;
}

/** Tab-separated text as copied from a spreadsheet: rows by line, cells by tab; a trailing newline is not a row. */
export function parseTsv(text: string): string[][] {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  if (lines.length > 1 && lines[lines.length - 1] === '') {
    lines.pop();
  }
  return lines.map((line) => line.split('\t'));
}

/** Pastes a block from the start cell, to the right and down, as far as rows and columns go. */
export function paste(
  edits: Edits,
  rows: readonly GridRow[],
  columns: readonly EditColumn[],
  start: { row: number; column: number },
  text: string,
) {
  let next = new Map(edits);
  parseTsv(text).forEach((cells, r) => {
    const row = rows[start.row + r];
    cells.forEach((cell, c) => {
      const column = columns[start.column + c];
      if (row && column) {
        next = setCell(next, row, column, cell);
      }
    });
  });
  return next;
}

/** Fill down: the first row's value in the column to the rest of the range, inclusive. */
export function fillDown(
  edits: Edits,
  rows: readonly GridRow[],
  column: EditColumn,
  from: number,
  to: number,
) {
  const [top, bottom] = from <= to ? [from, to] : [to, from];
  const value = cellValue(rows[top], column, edits);
  let next = new Map(edits);
  for (let i = top + 1; i <= bottom && i < rows.length; i++) {
    next = setCell(next, rows[i], column, value);
  }
  return next;
}

/** The plan operations for the edits, row by row, with the row each operation belongs to. */
export function toOperations(
  rows: readonly GridRow[],
  edits: Edits,
): { row: number; operation: NewOperation }[] {
  const result: { row: number; operation: NewOperation }[] = [];
  rows.forEach((row, index) => {
    const edit = edits.get(row.id);
    if (!edit) {
      return;
    }
    if (edit.name !== undefined) {
      result.push({
        row: index,
        operation: { kind: 'rename', type: row.type, objectId: row.id, name: edit.name },
      });
    }
    if (edit.lifecycle !== undefined) {
      result.push({
        row: index,
        operation: {
          kind: 'set_lifecycle',
          type: row.type,
          objectId: row.id,
          lifecycle: edit.lifecycle,
        },
      });
    }
    if (edit.attributes && Object.keys(edit.attributes).length) {
      result.push({
        row: index,
        operation: {
          kind: 'set_attributes',
          type: row.type,
          objectId: row.id,
          attributes: edit.attributes,
        },
      });
    }
  });
  return result;
}
