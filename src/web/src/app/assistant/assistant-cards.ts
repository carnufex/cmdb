/** A row of a card. A row with a ref opens that object. */
export interface CardRow {
  label: string;
  detail?: string;
  ref?: string;
}

/** What a tool's answer looks like in the chat: a heading and at most a few rows, from cmdb's answer and not from the model. */
export interface ChatCard {
  title: string;
  rows: readonly CardRow[];
  more?: string;
}

const MAX_ROWS = 6;
const REF = /^(site|equipment|cable|service|circuit|route-segment|service-point):\d+$/;

type Json = Record<string, unknown>;

const text = (v: unknown): string => (typeof v === 'string' ? v : '');
const number = (v: unknown): number => (typeof v === 'number' ? v : 0);
const list = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const count = (n: number): string => n.toLocaleString('sv-SE');
const ref = (v: unknown): string | undefined => {
  const s = text(v);
  return REF.test(s) ? s : undefined;
};

function card(title: string, rows: CardRow[], total = rows.length): ChatCard {
  return {
    title,
    rows: rows.slice(0, MAX_ROWS),
    more:
      total > Math.min(rows.length, MAX_ROWS)
        ? `och ${count(total - Math.min(rows.length, MAX_ROWS))} till`
        : undefined,
  };
}

function sites(a: Json): ChatCard {
  const total = number(a['total']);
  return card(
    total === 1 ? '1 site' : `${count(total)} siter`,
    list(a['sites']).map((s) => ({
      label: `${text(s['code'])} ${text(s['name'])}`.trim(),
      ref: ref(s['ref']),
    })),
    total,
  );
}

function summary(a: Json): ChatCard {
  const sum = typeof a['sum'] === 'number' ? ` · summa ${count(a['sum'])}` : '';
  return card(
    `${count(number(a['total']))} siter${sum}`,
    list(a['groups']).map((g) => ({
      label: text(g['key']),
      detail:
        count(number(g['sites'])) +
        (typeof g['sum'] === 'number' ? ` · summa ${count(g['sum'])}` : ''),
    })),
  );
}

function stations(a: unknown): ChatCard {
  const rows = (Array.isArray(a) ? (a as Json[]) : []).map((s) => ({
    label: `${text(s['code'])} ${text(s['name'])}`.trim(),
    detail: text(s['status']),
    ref: ref(s['station']),
  }));
  return card(rows.length === 1 ? '1 station' : `${rows.length} stationer`, rows);
}

function impact(a: Json): ChatCard {
  const affected = list(a['affectedServices']);
  return card(
    `Påverkar ${count(number(a['services']))} tjänster och ${count(number(a['circuits']))} förbindelser`,
    affected.map((s) => ({
      label: `${text(s['code'])} ${text(s['name'])}`.trim(),
      ref: ref(s['ref']),
    })),
    number(a['services']),
  );
}

function fault(a: Json): ChatCard {
  return card(
    `Prioritet ${text(a['priority'])} · ${count(number(a['servicesDown']))} tjänster nere, ${count(number(a['criticalDown']))} kritiska`,
    list(a['topServices']).map((s) => ({
      label: `${text(s['code'])} ${text(s['name'])}`.trim(),
      detail: text(s['redundancy']),
    })),
    number(a['affectedServices']),
  );
}

function incidents(a: unknown): ChatCard {
  const rows = (Array.isArray(a) ? (a as Json[]) : []).map((i) => ({
    label: `${text(i['number'])} ${text(i['siteCode'])}`.trim(),
    detail: `${text(i['priority'])} · ${text(i['description'])}`,
    ref: ref(i['site']),
  }));
  return card(rows.length === 1 ? '1 ärende' : `${rows.length} ärenden`, rows);
}

function risks(a: Json): ChatCard {
  return card(
    `${count(number(a['total']))} risker`,
    list(a['risks']).map((r) => ({
      label: text(r['title']),
      detail: `${text(r['siteCode'])} ${text(r['siteName'])}`.trim(),
      ref: ref(r['reference']),
    })),
    number(a['total']),
  );
}

function history(a: Json): ChatCard {
  return card(
    `${count(number(a['total']))} ändringar`,
    list(a['entries']).map((e) => ({
      label: `${text(e['at']).slice(0, 10)} ${text(e['actor'])}`.trim(),
      detail: list(e['changes'])
        .map((c) => text(c['field']))
        .join(', '),
    })),
    number(a['total']),
  );
}

const CARDS: Readonly<Record<string, (answer: never) => ChatCard>> = {
  find_sites: sites,
  show_on_map: sites,
  summarize_sites: summary,
  find_station: stations,
  impact: impact,
  fault_impact: fault,
  list_incidents: incidents,
  list_risks: risks,
  get_history: history,
};

/**
 * The card for a tool's answer, or null when the tool has none or the answer is not one: a refusal is text, and the agent's to
 * explain. The answer is cmdb's, under the person's own access, and only ever shown as text.
 */
export function cardFor(tool: string, content: unknown): ChatCard | null {
  const make = CARDS[tool];
  if (!make) {
    return null;
  }
  try {
    const answer: unknown = JSON.parse(String(content));
    return typeof answer === 'object' && answer !== null ? make(answer as never) : null;
  } catch {
    return null;
  }
}
