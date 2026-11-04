/** A row of what a tool answered. A row with a ref opens that object. */
export interface CardRow {
  label: string;
  detail?: string;
  ref?: string;
}

/** What a tool's answer looks like in the chat: one line, and the rows behind it. It is cmdb's answer, not the model's. */
export interface ChatCard {
  title: string;
  rows: readonly CardRow[];
  more?: string;
}

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
const named = (o: Json): string => `${text(o['code'])} ${text(o['name'])}`.trim();

function card(title: string, rows: CardRow[], total = rows.length): ChatCard {
  return {
    title,
    rows,
    more: total > rows.length ? `De ${count(rows.length)} första av ${count(total)}` : undefined,
  };
}

function plural(n: number, one: string, many: string): string {
  return n === 1 ? `1 ${one}` : `${count(n)} ${many}`;
}

function sites(a: Json): ChatCard {
  const total = number(a['total']);
  return card(
    plural(total, 'site', 'siter'),
    list(a['sites']).map((s) => ({ label: named(s), ref: ref(s['ref']) })),
    total,
  );
}

function summary(a: Json): ChatCard {
  const sum = typeof a['sum'] === 'number' ? ` · summa ${count(a['sum'])}` : '';
  return card(
    `${plural(number(a['total']), 'site', 'siter')}${sum}`,
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
    label: named(s),
    detail: text(s['status']),
    ref: ref(s['station']),
  }));
  return card(plural(rows.length, 'station', 'stationer'), rows);
}

function object(a: Json): ChatCard {
  const detail = (a['detail'] ?? {}) as Json;
  const label = named(detail) || text(a['ref']);
  return card(label, [{ label, ref: ref(a['ref']) }]);
}

function impact(a: Json): ChatCard {
  return card(
    `${plural(number(a['services']), 'tjänst', 'tjänster')}, ${plural(number(a['circuits']), 'krets', 'kretsar')}`,
    list(a['affectedServices']).map((s) => ({ label: named(s), ref: ref(s['ref']) })),
    number(a['services']),
  );
}

function fault(a: Json): ChatCard {
  return card(
    `Prioritet ${text(a['priority'])}, ${plural(number(a['servicesDown']), 'tjänst', 'tjänster')} nere`,
    list(a['topServices']).map((s) => ({ label: named(s), detail: text(s['redundancy']) })),
    number(a['affectedServices']),
  );
}

function incidents(a: unknown): ChatCard {
  const rows = (Array.isArray(a) ? (a as Json[]) : []).map((i) => ({
    label: `${text(i['number'])} ${text(i['siteCode'])}`.trim(),
    detail: `${text(i['priority'])} · ${text(i['description'])}`,
    ref: ref(i['site']),
  }));
  return card(plural(rows.length, 'ärende', 'ärenden'), rows);
}

function risks(a: Json): ChatCard {
  return card(
    plural(number(a['total']), 'risk', 'risker'),
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
    plural(number(a['total']), 'ändring', 'ändringar'),
    list(a['entries']).map((e) => ({
      label: `${text(e['at']).slice(0, 10)} ${text(e['actor'])}`.trim(),
      detail: list(e['changes'])
        .map((c) => text(c['field']))
        .join(', '),
    })),
    number(a['total']),
  );
}

function search(a: unknown): ChatCard {
  const hits = Array.isArray(a) ? (a as Json[]) : list((a as Json)['hits']);
  return card(
    plural(hits.length, 'träff', 'träffar'),
    hits.map((h) => ({ label: named(h), ref: ref(h['ref']) })),
  );
}

const CARDS: Readonly<Record<string, (answer: never) => ChatCard>> = {
  search: search,
  find_sites: sites,
  show_on_map: sites,
  summarize_sites: summary,
  find_station: stations,
  get_object: object,
  impact: impact,
  fault_impact: fault,
  list_incidents: incidents,
  list_risks: risks,
  get_history: history,
};

/** What each tool is called while it runs and afterwards. A tool without a line here is named as it is. */
const STEPS: Readonly<Record<string, readonly [string, string]>> = {
  search: ['Söker', 'Sökte'],
  find_sites: ['Söker siter', 'Sökte siter'],
  show_on_map: ['Söker siter', 'Sökte siter'],
  summarize_sites: ['Räknar siter', 'Räknade siter'],
  find_station: ['Söker station', 'Sökte station'],
  station_overview: ['Läser stationen', 'Läste stationen'],
  get_object: ['Läser objektet', 'Läste objektet'],
  open_object: ['Öppnar objektet', 'Öppnade objektet'],
  impact: ['Analyserar påverkan', 'Analyserade påverkan'],
  fault_impact: ['Analyserar felet', 'Analyserade felet'],
  trace: ['Spårar', 'Spårade'],
  neighbourhood: ['Läser grannskapet', 'Läste grannskapet'],
  describe_catalog: ['Läser katalogen', 'Läste katalogen'],
  my_access: ['Kontrollerar din behörighet', 'Kontrollerade din behörighet'],
  list_incidents: ['Läser ärenden', 'Läste ärenden'],
  list_risks: ['Läser risker', 'Läste risker'],
  risk_details: ['Läser risken', 'Läste risken'],
  get_history: ['Läser historiken', 'Läste historiken'],
  list_reservations: ['Läser reservationer', 'Läste reservationer'],
  list_plans: ['Läser planer', 'Läste planer'],
  preview_plan: ['Läser planen', 'Läste planen'],
  request_change: ['Förbereder ändringen', 'Förberedde ändringen'],
};

export const stepLabel = (tool: string, done: boolean): string =>
  STEPS[tool]?.[done ? 1 : 0] ?? `${done ? 'Körde' : 'Kör'} ${tool}`;

/** The answer as JSON, or null for a refusal, which is plain text and the agent's to explain. */
export function answerOf(content: unknown): unknown {
  try {
    const answer: unknown = JSON.parse(String(content));
    return typeof answer === 'object' && answer !== null ? answer : null;
  } catch {
    return null;
  }
}

/** The rows behind a tool's answer, or null when the tool has none. Only ever shown as text. */
export function cardFor(tool: string, content: unknown): ChatCard | null {
  const make = Object.hasOwn(CARDS, tool) ? CARDS[tool] : null;
  const answer = answerOf(content);
  return make && answer ? make(answer as never) : null;
}
