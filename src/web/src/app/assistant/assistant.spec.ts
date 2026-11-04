import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { HttpAgent } from '@ag-ui/client';
import { Auth } from '../auth/auth';
import { RUNTIME_CONFIG } from '../config';
import { MapView } from '../map/map-view';
import { cardFor } from './assistant-cards';
import { AGENT_FACTORY, Assistant, siteQueryFrom } from './assistant';
import { LOTS_SIGN_IN, LotsAuth } from './lots-auth';

const person = (sub: string, expired = false) => ({
  access_token: `token-of-${sub}`,
  expired,
  profile: { sub, name: 'Demo Full' },
});

/** The stream as Lots writes it (Lots/Features/Channels/AgUiEndpoints.cs), read by the real AG-UI client. */
function stream(...events: object[]): Response {
  const body = events.map((e) => `data: ${JSON.stringify(e)}\n\n`).join('') + ': keep-alive\n\n';
  return new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } });
}

const started = { type: 'RUN_STARTED', threadId: 't', runId: 'r' };
const finished = { type: 'RUN_FINISHED', threadId: 't', runId: 'r' };

function showOnMap(args: object, result: string): object[] {
  return [
    { type: 'TOOL_CALL_START', toolCallId: 'c1', toolCallName: 'show_on_map' },
    { type: 'TOOL_CALL_ARGS', toolCallId: 'c1', delta: JSON.stringify(args) },
    { type: 'TOOL_CALL_END', toolCallId: 'c1' },
    { type: 'TOOL_CALL_RESULT', messageId: 'r1', toolCallId: 'c1', role: 'tool', content: result },
  ];
}

describe('Assistant', () => {
  let requests: { url: string; init: RequestInit }[];
  let answer: () => Response | Promise<Response>;
  let signinPopup: ReturnType<typeof vi.fn>;
  let signinSilent: ReturnType<typeof vi.fn>;
  let removeUser: ReturnType<typeof vi.fn>;
  let cmdbUser: string;

  function setup(lotsUrl = 'https://lots.example.org/'): Assistant {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: RUNTIME_CONFIG,
          useValue: {
            oidcAuthority: '',
            oidcClientId: '',
            lotsUrl,
            lotsAuthority: 'https://idp.example.org/application/o/lots/',
            lotsClientId: 'lots',
            lotsContext: 'cmdb',
          },
        },
        { provide: Auth, useValue: { user: () => ({ profile: { sub: cmdbUser } }) } },
        { provide: LOTS_SIGN_IN, useValue: { signinPopup, signinSilent, removeUser } },
        {
          provide: AGENT_FACTORY,
          useValue: async (url: string, threadId: string) =>
            new HttpAgent({
              url,
              threadId,
              fetch: async (target: RequestInfo | URL, init?: RequestInit) => {
                requests.push({ url: String(target), init: init! });
                return answer();
              },
            } as ConstructorParameters<typeof HttpAgent>[0]),
        },
      ],
    });
    return TestBed.inject(Assistant);
  }

  async function signedIn(): Promise<Assistant> {
    const assistant = setup();
    await TestBed.inject(LotsAuth).signIn();
    return assistant;
  }

  const settle = () => new Promise((resolve) => setTimeout(resolve));

  beforeEach(() => {
    requests = [];
    cmdbUser = 'alice';
    answer = () => stream(started, finished);
    signinPopup = vi.fn(async () => person('alice'));
    signinSilent = vi.fn(async () => person('alice'));
    removeUser = vi.fn(async () => undefined);
  });

  it('is hidden without a configured Lots, and makes no sign-in client', () => {
    const made = vi.fn();
    TestBed.overrideProvider(LOTS_SIGN_IN, { useFactory: made });
    expect(setup('').enabled).toBe(false);
    expect(TestBed.inject(LotsAuth).access()).toBe('signed-out');
    expect(made).not.toHaveBeenCalled();
  });

  it('asks as the signed-in person, with their Lots token and only this turn', async () => {
    const assistant = await signedIn();
    answer = () =>
      stream(
        started,
        { type: 'TEXT_MESSAGE_START', messageId: 'm1', role: 'assistant' },
        { type: 'TEXT_MESSAGE_CONTENT', messageId: 'm1', delta: 'Hej ' },
        { type: 'TEXT_MESSAGE_CONTENT', messageId: 'm1', delta: 'Alice' },
        { type: 'TEXT_MESSAGE_END', messageId: 'm1' },
        finished,
      );

    await assistant.ask('Vem är jag?');
    await assistant.ask('Och nu?');

    expect(requests[0].url).toBe('https://lots.example.org/ag-ui');
    expect((requests[0].init.headers as Record<string, string>)['Authorization']).toBe(
      'Bearer token-of-alice',
    );
    const second = JSON.parse(requests[1].init.body as string);
    expect(second.messages.map((m: { content: string }) => m.content)).toEqual(['Och nu?']);
    expect(second.forwardedProps).toEqual({ context: 'cmdb' });
    expect(second.threadId).toBe(JSON.parse(requests[0].init.body as string).threadId);
    expect(assistant.lines().map((l) => l.text)).toEqual([
      'Vem är jag?',
      'Hej Alice',
      'Och nu?',
      'Hej Alice',
    ]);
  });

  it('refuses a Lots session for anyone else than the person signed in to cmdb', async () => {
    cmdbUser = 'alice';
    signinPopup = vi.fn(async () => person('mallory'));
    setup();
    const lots = TestBed.inject(LotsAuth);

    await lots.signIn();

    expect(lots.access()).toBe('other-person');
    expect(await lots.token()).toBeNull();
    expect(removeUser).toHaveBeenCalled();
  });

  it('renews an expired token and asks the person to sign in when that fails', async () => {
    signinPopup = vi.fn(async () => person('alice', true));
    setup();
    const lots = TestBed.inject(LotsAuth);
    await lots.signIn();

    expect(await lots.token()).toBe('token-of-alice');
    expect(signinSilent).toHaveBeenCalledTimes(1);

    signinPopup = vi.fn(async () => person('alice', true));
    TestBed.resetTestingModule();
    signinSilent = vi.fn(async () => {
      throw new Error('expired');
    });
    setup();
    const again = TestBed.inject(LotsAuth);
    await again.signIn();
    expect(await again.token()).toBeNull();
    expect(again.access()).toBe('signed-out');
  });

  it('marks the map from our own API under the person, not from what the model wrote', async () => {
    const assistant = await signedIn();
    const map = TestBed.inject(MapView);
    const mark = vi.spyOn(map, 'mark');
    answer = () =>
      stream(
        started,
        ...showOnMap(
          {
            place: 'Gävle',
            siteTypes: ['radio'],
            points: [[1, 2, 3]],
            url: 'https://evil.example',
          },
          '{"total":2,"sites":[]}',
        ),
        finished,
      );

    await assistant.ask('Visa mig alla siter i Gävle');
    await settle();

    const http = TestBed.inject(HttpTestingController);
    const post = http.expectOne('/api/query/sites');
    expect(post.request.body.place).toBe('Gävle');
    expect(post.request.body.siteTypes).toEqual(['radio']);
    expect(post.request.body.points).toBeUndefined();
    expect(post.request.body.url).toBeUndefined();
    post.flush({
      total: 2,
      sites: [],
      points: [
        [7, 100, 200],
        [8, 110, 210],
      ],
      extent: [100, 200, 110, 210],
      elapsedMs: 1,
    });
    await settle();

    expect(mark).toHaveBeenCalledWith(
      [
        [7, 100, 200],
        [8, 110, 210],
      ],
      [100, 200, 110, 210],
    );
    const step = assistant.lines().find((l) => l.role === 'steps')!.steps![0];
    expect(step.sites!.marked).toBe(2);
    expect(step.open).toBe(true);
  });

  it('marks nothing for a refused call or another tool', async () => {
    const assistant = await signedIn();
    answer = () =>
      stream(
        started,
        ...showOnMap({ place: 'Gävle' }, 'Denied: no role grants Read'),
        { type: 'TOOL_CALL_START', toolCallId: 'c2', toolCallName: 'find_sites' },
        { type: 'TOOL_CALL_END', toolCallId: 'c2' },
        {
          type: 'TOOL_CALL_RESULT',
          messageId: 'r2',
          toolCallId: 'c2',
          role: 'tool',
          content: '{"total":1}',
        },
        finished,
      );

    await assistant.ask('Visa');
    await settle();

    TestBed.inject(HttpTestingController).expectNone('/api/query/sites');
  });

  it('tells what the map could not show', async () => {
    const assistant = await signedIn();
    answer = () => stream(started, ...showOnMap({ place: 'Ankeborg' }, '{"total":0}'), finished);

    await assistant.ask('Visa');
    await settle();
    TestBed.inject(HttpTestingController)
      .expectOne('/api/query/sites')
      .flush(
        { errors: { generalErrors: ["Unknown place 'Ankeborg'."] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle();

    const step = assistant.lines().find((l) => l.role === 'steps')!.steps![0];
    expect(step.problem).toBe("Unknown place 'Ankeborg'.");
    expect(step.open).toBe(true);
  });

  it('offers the connection in Lots, and a link only into that Lots', async () => {
    const assistant = await signedIn();
    answer = () =>
      stream(
        started,
        {
          type: 'CUSTOM',
          name: 'lots.connect',
          value: { servers: ['cmdb'], url: 'https://lots.example.org/#/account/connections' },
        },
        { type: 'CUSTOM', name: 'lots.approval', value: { url: 'https://evil.example/#/runs/1' } },
        finished,
      );

    await assistant.ask('Visa');

    const [, connect, approval] = assistant.lines();
    expect(connect.link?.href).toBe('https://lots.example.org/#/account/connections');
    expect(approval.link).toBeUndefined();
  });

  it('shows a run that fails, and says what a refusal means', async () => {
    const assistant = await signedIn();
    answer = () => stream(started, { type: 'RUN_ERROR', message: 'Modellen svarade inte' });
    await assistant.ask('Visa');
    expect(assistant.lines().at(-1)).toMatchObject({
      role: 'error',
      text: 'Modellen svarade inte',
    });

    answer = () => new Response('{"error":{"message":"no"}}', { status: 403 });
    await assistant.ask('Igen');
    expect(assistant.lines().at(-1)!.text).toContain('tillgång till kontexten cmdb');
    expect(assistant.busy()).toBe(false);
  });

  function toolCall(name: string, args: object, result: string): object[] {
    return [
      { type: 'TOOL_CALL_START', toolCallId: 'c9', toolCallName: name },
      { type: 'TOOL_CALL_ARGS', toolCallId: 'c9', delta: JSON.stringify(args) },
      { type: 'TOOL_CALL_END', toolCallId: 'c9' },
      {
        type: 'TOOL_CALL_RESULT',
        messageId: 'r9',
        toolCallId: 'c9',
        role: 'tool',
        content: result,
      },
    ];
  }

  const http = () => TestBed.inject(HttpTestingController);

  async function askedAboutChange(plan: object, operations: object[]): Promise<Assistant> {
    const assistant = await signedIn();
    answer = () =>
      stream(
        started,
        ...toolCall(
          'request_change',
          { title: 'x', operations: [] },
          JSON.stringify({ plan: { ref: 'plan:7' } }),
        ),
        finished,
      );
    await assistant.ask('Byt namn');
    await settle();
    http().expectOne('/api/plans/7').flush({ plan, dependencies: [], operations });
    http().expectOne('/api/me').flush({ username: 'demo-full' });
    await settle();
    return assistant;
  }

  it('asks before a change, with what the plan in cmdb says and not what the model wrote', async () => {
    const assistant = await askedAboutChange({ id: 7, status: 'draft', createdBy: 'demo-full' }, [
      { summary: 'Byt namn på AGG-000315 till X', problem: null },
    ]);
    const change = assistant.lines().find((l) => l.change)!.change!;
    expect(change.changes).toEqual(['Byt namn på AGG-000315 till X']);
    expect(change.state).toBe('asking');
  });

  it("writes on yes with the person's own token, and only then", async () => {
    const assistant = await askedAboutChange({ id: 7, status: 'draft', createdBy: 'demo-full' }, [
      { summary: 'Byt namn', problem: null },
    ]);
    const line = assistant.lines().find((l) => l.change)!;
    http().expectNone('/api/plans/7/apply');
    const answered = assistant.answer(line.id, true);
    http().expectOne({ method: 'POST', url: '/api/plans/7/apply' }).flush({});
    await answered;
    expect(assistant.lines().find((l) => l.change)!.change!.state).toBe('written');
  });

  it('takes a typed no as the answer, without asking the model', async () => {
    const assistant = await askedAboutChange({ id: 7, status: 'draft', createdBy: 'demo-full' }, [
      { summary: 'Byt namn', problem: null },
    ]);
    const before = requests.length;
    const answered = assistant.ask('nej');
    http().expectOne({ method: 'POST', url: '/api/plans/7/cancel' }).flush({});
    await answered;
    expect(requests.length).toBe(before);
    expect(assistant.lines().find((l) => l.change)!.change!.state).toBe('declined');
  });

  it('cannot approve a change with a problem', async () => {
    const assistant = await askedAboutChange({ id: 7, status: 'draft', createdBy: 'demo-full' }, [
      { summary: 'Byt namn', problem: 'Finns inte längre' },
    ]);
    const line = assistant.lines().find((l) => l.change)!;
    await assistant.answer(line.id, true);
    http().expectNone('/api/plans/7/apply');
    expect(line.change!.state).toBe('asking');
  });

  it("asks about nothing but a draft of the person's own", async () => {
    const assistant = await askedAboutChange(
      { id: 7, status: 'draft', createdBy: 'someone-else' },
      [{ summary: 'Byt namn', problem: null }],
    );
    expect(assistant.lines().some((l) => l.change)).toBe(false);
    expect(assistant.lines().some((l) => l.role === 'error')).toBe(true);
  });

  it('opens only an object reference', () => {
    const assistant = setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    assistant.openObject('javascript:alert(1)');
    expect(navigate).not.toHaveBeenCalled();
    assistant.openObject('site:12');
    expect(navigate).toHaveBeenCalledWith(
      ['/'],
      expect.objectContaining({ queryParams: { p: 'site:12' } }),
    );
  });

  it('tells the agent what the person is looking at', async () => {
    const assistant = await signedIn();
    TestBed.inject(MapView).extent.set([1.4, 2, 3, 4]);
    answer = () => stream(started, finished);
    await assistant.ask('Vad finns här?');
    const body = JSON.parse(String(requests.at(-1)!.init.body)) as {
      context: { description: string; value: string }[];
    };
    expect(body.context[0].value).toBe('[1, 2, 3, 4]');
  });

  it("draws the rows from the tool's answer, and none for a refusal", () => {
    expect(
      cardFor(
        'find_sites',
        JSON.stringify({ total: 9, sites: [{ ref: 'site:1', code: 'A', name: 'B' }] }),
      ),
    ).toEqual({
      title: '9 siter',
      rows: [{ label: 'A B', ref: 'site:1' }],
      more: 'De 1 första av 9',
    });
    expect(cardFor('find_sites', 'No such thing')).toBeNull();
    expect(cardFor('unknown_tool', '{}')).toBeNull();
    expect(
      cardFor(
        'find_sites',
        JSON.stringify({ total: 1, sites: [{ ref: 'javascript:x', code: 'A' }] }),
      )!.rows[0].ref,
    ).toBeUndefined();
  });

  it('numbers the sources as Lots does: only calls that gave an answer', async () => {
    const assistant = await signedIn();
    answer = () =>
      stream(
        started,
        ...toolCall('find_station', { query: 'x' }, 'Refused by policy'),
        ...toolCall('find_station', { query: 'y' }, '[{"station":"site:1","code":"A","name":"B"}]'),
        { type: 'TEXT_MESSAGE_START', messageId: 'm1', role: 'assistant' },
        { type: 'TEXT_MESSAGE_CONTENT', messageId: 'm1', delta: 'Det är A [t1].' },
        { type: 'TEXT_MESSAGE_END', messageId: 'm1' },
        finished,
      );
    await assistant.ask('Vilken?');
    const line = assistant.lines().find((l) => l.role === 'steps')!;
    expect(line.steps!.map((s) => [s.state, s.source])).toEqual([
      ['failed', undefined],
      ['done', 1],
    ]);
    expect(line.steps![1].card!.rows[0]).toEqual({ label: 'A B', detail: '', ref: 'site:1' });

    assistant.showSource(line.run, 1);
    const shown = assistant.source(line.run, 1)!;
    expect(shown.open).toBe(true);
    expect(assistant.focused()).toBe(shown.id);
    expect(assistant.source(line.run, 9)).toBeNull();
  });

  it('lists every match from our own API under the step that marked them', async () => {
    const assistant = await signedIn();
    answer = () =>
      stream(
        started,
        ...toolCall('show_on_map', { place: 'Gävle' }, '{"total":2,"sites":[]}'),
        finished,
      );
    await assistant.ask('Visa siterna');
    await settle();
    const post = TestBed.inject(HttpTestingController).expectOne('/api/query/sites');
    expect(post.request.body.limit).toBe(1000);
    post.flush({
      total: 2,
      sites: [
        { id: 1, code: 'A', name: 'a', lifecycle: 'in_service', x: 1, y: 2 },
        { id: 2, code: 'B', name: 'b', lifecycle: 'planned', x: 3, y: 4 },
      ],
      points: [
        [1, 1, 2],
        [2, 3, 4],
      ],
      extent: [1, 2, 3, 4],
    });
    await settle();
    const step = assistant.lines().find((l) => l.role === 'steps')!.steps![0];
    expect(step.open).toBe(true);
    expect(step.sites!.rows.length).toBe(2);
    expect(step.sites!.marked).toBe(2);
  });

  it('stops the run in Lots too, with the Lots token and not through cmdb', async () => {
    const assistant = await signedIn();
    const cancelled = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null));
    let finish: (r: Response) => void = () => undefined;
    answer = () => new Promise<Response>((resolve) => (finish = resolve));
    const asked = assistant.ask('Något långt');
    await settle();
    (assistant as unknown as { running: string }).running = 'run-1';
    await assistant.stop();
    expect(cancelled).toHaveBeenCalledWith(
      'https://lots.example.org/runs/run-1/cancel',
      expect.objectContaining({
        method: 'POST',
        headers: { Authorization: 'Bearer token-of-alice' },
      }),
    );
    finish(stream(started, finished));
    await asked.catch(() => undefined);
    expect(assistant.lines().some((l) => l.role === 'error')).toBe(false);
    cancelled.mockRestore();
  });

  it('says nothing more than stopped when the stream ends on the stop', async () => {
    const assistant = await signedIn();
    const fetched = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null));
    let finish: (r: Response) => void = () => undefined;
    answer = () => new Promise<Response>((resolve) => (finish = resolve));
    const asked = assistant.ask('Något långt');
    await settle();
    await assistant.stop();
    finish(stream(started, { type: 'RUN_ERROR', message: 'signal is aborted without reason' }));
    await asked.catch(() => undefined);
    expect(assistant.lines().some((l) => l.role === 'error')).toBe(false);
    expect(assistant.lines().at(-1)!.text).toBe('Stoppad.');
    fetched.mockRestore();
  });

  it('starts over with a new thread', async () => {
    const assistant = await signedIn();
    answer = () => stream(started, finished);
    await assistant.ask('Något');
    const first = JSON.parse(String(requests.at(-1)!.init.body)).threadId;
    assistant.newChat();
    expect(assistant.lines()).toEqual([]);
    await assistant.ask('Något annat');
    expect(JSON.parse(String(requests.at(-1)!.init.body)).threadId).not.toBe(first);
  });

  it('keeps only known fields of a tool call', () => {
    expect(
      siteQueryFrom({
        place: 'Gävle',
        lifecycles: ['in_service'],
        siteTypes: 'radio',
        equipment: [{ category: 'radio' }],
        limit: 5000,
        extra: true,
      }),
    ).toEqual({
      limit: 1000,
      place: 'Gävle',
      lifecycles: ['in_service'],
      siteTypes: undefined,
      serviceTypes: undefined,
      equipment: [{ category: 'radio' }],
      siteAttributes: undefined,
    });
    expect(siteQueryFrom(null)).toMatchObject({ limit: 1000, place: undefined });
  });
});
