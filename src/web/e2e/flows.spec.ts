import { expect, Page, test } from '@playwright/test';

/**
 * The most important flows in a real browser (#288), against the synthetic small network (seed 1) that scripts/e2e.sh
 * loads, signed in as the stand-in provider's planner (cmdb-full).
 */

const idp = process.env.E2E_IDP ?? 'http://127.0.0.1:8790/application/o/cmdb/';

/** The web app's runtime settings for the stand-in provider and no background map: nothing is fetched from outside. */
async function open(page: Page, path = '/'): Promise<void> {
  await page.route('**/config.json', (route) =>
    route.fulfill({
      json: {
        oidcAuthority: idp,
        oidcClientId: 'cmdb-web',
        basemap: 'none',
        voiceAgentId: '',
        voiceNocAgentId: '',
      },
    }),
  );
  await page.goto(path);
  await expect(page.getByRole('combobox', { name: 'Sök eller kör en åtgärd' })).toBeVisible();
  await expect(page.locator('cmdb-map .ol-viewport')).toBeVisible();
}

/** The map's canvas, and a point on it as a fraction of its size. */
async function mapPoint(page: Page, fx: number, fy: number): Promise<{ x: number; y: number }> {
  const box = (await page.locator('cmdb-map .ol-viewport').boundingBox())!;
  return { x: box.x + box.width * fx, y: box.y + box.height * fy };
}

test('quick search finds a site and opens its panel', async ({ page }) => {
  await open(page);
  const search = page.getByRole('combobox', { name: 'Sök eller kör en åtgärd' });
  await search.fill('HUB-001');
  await expect(page.getByRole('option').filter({ hasText: 'HUB-001' }).first()).toBeVisible();
  await search.press('Enter');
  await expect(page).toHaveURL(/p=site:\d+/);
  await expect(page.locator('cmdb-site-panel')).toContainText('HUB-001');
});

test('the map is drawn as it was known on an earlier day, and a site shows its history', async ({
  page,
}) => {
  await open(page);
  await page.getByRole('button', { name: 'Tidsresa' }).click();
  await page.getByLabel('Datum').fill('1990-01-01');
  await page.getByRole('radio', { name: /Vad vi visste/ }).check();
  const tile = page.waitForRequest(
    (r) => r.url().includes('/api/tiles/') && r.url().includes('mode=knowledge'),
  );
  await page.getByRole('button', { name: 'Visa kartan' }).click();
  await tile;
  const banner = page.getByRole('status').filter({ hasText: 'Nätet som CMDB:n beskrev det' });
  await expect(banner).toBeVisible();
  await banner.getByRole('button', { name: 'Tillbaka till nu' }).click();
  await expect(banner).toBeHidden();

  const search = page.getByRole('combobox', { name: 'Sök eller kör en åtgärd' });
  await search.fill('HUB-001');
  await expect(page.getByRole('option').filter({ hasText: 'HUB-001' }).first()).toBeVisible();
  await search.press('Enter');
  const panel = page.locator('cmdb-site-panel');
  await panel.getByText('Historik').click();
  await expect(panel).toContainText('Första registrerade version');
});

test('the map’s context menu offers what can be done on an empty spot', async ({ page }) => {
  await open(page);
  const at = await mapPoint(page, 0.05, 0.5);
  await page.mouse.click(at.x, at.y, { button: 'right' });
  const menu = page.getByRole('menu');
  await expect(menu).toBeVisible();
  await expect(menu.getByRole('menuitem', { name: 'Mät avstånd' })).toBeVisible();
  await expect(menu.getByRole('menuitem', { name: /Ny site här/ })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
});

test('a plan is made from the short row and opens on its overview', async ({ page }) => {
  await open(page, '/?tool=plans&size=half');
  await page.getByRole('button', { name: 'Ny plan' }).first().click();
  await page.getByRole('textbox', { name: 'Planens namn' }).fill('E2E: plan');
  await page.getByRole('button', { name: 'Skapa och visa' }).click();
  await expect(page).toHaveURL(/plan=\d+/);
  const overview = page.getByRole('region', { name: 'Aktiv plan' });
  await expect(overview).toContainText('E2E: plan');
  await expect(overview.getByRole('button', { name: 'Lägg till ändringar' })).toBeVisible();
});

test('a new cable is drawn along the conduit, taken back step by step, and added to the plan', async ({
  page,
}) => {
  await open(page, '/?tool=plans&size=half');
  await page.getByRole('button', { name: 'Ny plan' }).first().click();
  await page.getByRole('textbox', { name: 'Planens namn' }).fill('E2E: ritad kabel');
  await page.getByRole('button', { name: 'Skapa och visa' }).click();
  await expect(page).toHaveURL(/plan=\d+/);

  await page.getByRole('tab', { name: /Lägg till/ }).click();
  await page.getByRole('tab', { name: 'Ny kabel' }).click();
  await page.locator('cmdb-plan-create input[name=a]').fill('HUB-001');
  await page.getByRole('button', { name: /Rita vägen i kartan från HUB-001/ }).click();
  const bar = page.getByRole('region', { name: 'Rita kabel' });
  await expect(bar).toContainText('Ny kabel från HUB-001');

  // Where HUB-001 is, from the quick search's answer.
  const search = page.getByRole('combobox', { name: 'Sök eller kör en åtgärd' });
  const answer = page.waitForResponse((r) => r.url().includes('/api/search') && r.ok());
  await search.fill('HUB-001');
  const hits = (await (await answer).json()) as { code: string; x: number; y: number }[];
  const hub = hits.find((h) => h.code === 'HUB-001')!;
  await search.press('Escape');
  await search.blur();

  // A route segment out of HUB-001, found on screen the way a click finds it, then clicked with the mouse.
  const segment = await page.evaluate(async ({ x, y }) => {
    const ng = (window as unknown as { ng: { getComponent: (e: Element) => any } }).ng;
    const map = ng.getComponent(document.querySelector('cmdb-map')!);
    const view = map.map.getView();
    view.setCenter([x, y]);
    view.setZoom(12);
    await new Promise((r) => setTimeout(r, 3000));
    const [w, h] = map.map.getSize();
    for (let r = 4; r < Math.min(w, h) / 2; r += 4) {
      for (let a = 0; a < 360; a += 10) {
        const px = [
          w / 2 + r * Math.cos((a * Math.PI) / 180),
          h / 2 + r * Math.sin((a * Math.PI) / 180),
        ];
        // The same question a click asks: a site wins within a few pixels, so only a segment alone counts.
        const step = map.drawStep(px, map.map.getCoordinateFromPixel(px));
        const found: number | undefined = step.kind === 'segment' ? step.id : undefined;
        if (found !== undefined) {
          return { x: px[0], y: px[1], id: found };
        }
      }
    }
    return null;
  }, hub);
  expect(segment).not.toBeNull();
  const box = (await page.locator('cmdb-map .ol-viewport').boundingBox())!;
  await page.mouse.click(box.x + segment!.x, box.y + segment!.y);
  await expect(bar).toContainText('i kanalisation');
  await expect(bar).toContainText(/rör ledigt|inga lediga rör/);

  // A free break point, then Backspace takes it back.
  // Right of the middle: clear of the legend (bottom left), the drawing bar (top) and the lens switch (top right).
  const free = await mapPoint(page, 0.85, 0.6);
  await page.mouse.click(free.x, free.y);
  await expect(bar.getByText(/ny sträcka, grävd/)).toBeVisible();
  await expect(bar.getByText('2 steg')).toBeVisible();
  await page.keyboard.press('Backspace');
  await expect(bar.getByText('1 steg')).toBeVisible();

  // The segment's far end finishes the cable; it goes into the plan with its route. The preview for the steps left
  // after Backspace arrives asynchronously.
  const farEnd = () =>
    page.evaluate(() => {
      const ng = (window as unknown as { ng: { getComponent: (e: Element) => any } }).ng;
      const map = ng.getComponent(document.querySelector('cmdb-map')!);
      return (map.cableDrawing.route()?.end?.id as number | undefined) ?? null;
    });
  await expect.poll(farEnd).not.toBeNull();
  const end = await farEnd();
  await page.evaluate((id) => {
    const ng = (window as unknown as { ng: { getComponent: (e: Element) => any } }).ng;
    ng.getComponent(document.querySelector('cmdb-map')!).cableDrawing.click({ kind: 'site', id });
  }, end);
  await expect(bar.getByRole('button', { name: 'Lägg till kabel' })).toBeEnabled();
  await bar.getByRole('button', { name: 'Lägg till kabel' }).click();
  await expect(bar).toBeHidden();
  await page.getByRole('tab', { name: /Ändringar/ }).click();
  await expect(page.locator('cmdb-plan-changes')).toContainText('Rita om');

  // Escape stops a drawing without adding anything.
  await page.getByRole('tab', { name: /Lägg till/ }).click();
  await page.getByRole('tab', { name: 'Ny kabel' }).click();
  await page.locator('cmdb-plan-create input[name=a]').fill('HUB-001');
  await page.getByRole('button', { name: /Rita vägen i kartan från HUB-001/ }).click();
  await expect(bar).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(bar).toBeHidden();
});

test('the chat asks Lots as the signed-in person and marks what it shows on the map', async ({
  page,
}) => {
  const lots = 'http://127.0.0.1:8791';
  const asked: { authorization: string | undefined; body: Record<string, unknown> }[] = [];
  await page.route(`${lots}/ag-ui`, async (route) => {
    const cors = { 'Access-Control-Allow-Origin': new URL(page.url()).origin };
    if (route.request().method() === 'OPTIONS') {
      await route.fulfill({
        status: 204,
        headers: {
          ...cors,
          'Access-Control-Allow-Methods': 'POST',
          'Access-Control-Allow-Headers': 'Authorization, Content-Type, Accept',
        },
      });
      return;
    }
    asked.push({
      authorization: route.request().headers()['authorization'],
      body: route.request().postDataJSON(),
    });
    const thread = {
      threadId: '00000000-0000-4000-8000-000000000001',
      runId: '00000000-0000-4000-8000-000000000002',
    };
    const events = [
      { type: 'RUN_STARTED', ...thread },
      { type: 'TOOL_CALL_START', toolCallId: 'c1', toolCallName: 'show_on_map' },
      { type: 'TOOL_CALL_ARGS', toolCallId: 'c1', delta: JSON.stringify({ place: 'Stockholm' }) },
      { type: 'TOOL_CALL_END', toolCallId: 'c1' },
      {
        type: 'TOOL_CALL_RESULT',
        messageId: 'r1',
        toolCallId: 'c1',
        role: 'tool',
        content: '{"total":1,"sites":[]}',
      },
      { type: 'TEXT_MESSAGE_START', messageId: 'm1', role: 'assistant' },
      { type: 'TEXT_MESSAGE_CONTENT', messageId: 'm1', delta: 'Här är siterna i Stockholm.' },
      { type: 'TEXT_MESSAGE_END', messageId: 'm1' },
      { type: 'RUN_FINISHED', ...thread },
    ];
    await route.fulfill({
      status: 200,
      headers: { ...cors, 'Content-Type': 'text/event-stream' },
      body: events.map((e) => `data: ${JSON.stringify(e)}\n\n`).join(''),
    });
  });
  await page.route('**/config.json', (route) =>
    route.fulfill({
      json: {
        oidcAuthority: idp,
        oidcClientId: 'cmdb-web',
        basemap: 'none',
        lotsUrl: lots,
        lotsAuthority: idp.replace('/cmdb/', '/lots/'),
        lotsClientId: 'lots',
        lotsContext: 'cmdb',
      },
    }),
  );
  await page.goto('/');
  await expect(page.locator('cmdb-map .ol-viewport')).toBeVisible();

  // Signing in to Lots asks nothing in the provider's session (ADR-0025).
  await page.getByRole('button', { name: 'Chatt', exact: true }).click();
  const chat = page.getByRole('region', { name: 'Chatt med Lots' });
  await expect(chat.getByText('Du frågar som E2E Planerare')).toBeVisible();

  await chat.getByRole('textbox', { name: 'Din fråga' }).fill('Visa mig alla siter i Stockholm');
  await chat.getByRole('textbox', { name: 'Din fråga' }).press('Enter');
  await expect(chat.getByText('Här är siterna i Stockholm.')).toBeVisible();
  // Every match is in the chat, in a list of its own, and opens from there.
  const rows = chat.locator('cmdb-assistant-sites .row');
  if (await rows.count()) {
    await rows.first().click();
    await expect(page).toHaveURL(/[?&]p=site(:|%3A)\d+/);
  }
  await expect(
    chat.getByText(/\d+ siter? markerade på kartan\.|Inga siter att markera/),
  ).toBeVisible();

  // A token issued for Lots, not cmdb's own (ADR-0023), and the context and thread the protocol carries.
  const token = asked[0].authorization!.replace('Bearer ', '');
  const claims = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());
  expect(claims.aud).toBe('lots');
  expect(claims.sub).toBe('e2e-planerare');
  expect(asked[0].body['forwardedProps']).toEqual({ context: 'cmdb' });

  // A new chat starts over, with the questions to begin from.
  await chat.getByRole('button', { name: 'Ny chatt' }).click();
  await expect(chat.getByRole('button', { name: 'Vad får jag se?' })).toBeVisible();
});

test('a change asked for in the chat waits for a yes, and the yes writes it as the person', async ({
  page,
}) => {
  const lots = 'http://127.0.0.1:8791';
  let cmdbToken = '';
  page.on('request', (r) => {
    const authorization = r.headers()['authorization'];
    if (r.url().includes('/api/') && authorization) {
      cmdbToken = authorization;
    }
  });
  const name = `Omdöpt ${Date.now()}`;
  let plan = 0;
  let site = 0;
  await page.route(`${lots}/ag-ui`, async (route) => {
    const cors = { 'Access-Control-Allow-Origin': new URL(page.url()).origin };
    if (route.request().method() === 'OPTIONS') {
      await route.fulfill({
        status: 204,
        headers: {
          ...cors,
          'Access-Control-Allow-Methods': 'POST',
          'Access-Control-Allow-Headers': 'Authorization, Content-Type, Accept',
        },
      });
      return;
    }
    // What request_change leaves behind: a draft of the person's own.
    const headers = { Authorization: cmdbToken };
    const found = await page.request.post('/api/query/sites', { headers, data: { limit: 1 } });
    site = (await found.json()).sites[0].id;
    const made = await page.request.post('/api/plans', { headers, data: { name: 'Byt namn' } });
    plan = (await made.json()).id;
    const added = await page.request.post(`/api/plans/${plan}/operations`, {
      headers,
      data: { kind: 'rename', type: 'site', objectId: site, name },
    });
    expect(added.ok()).toBeTruthy();
    const thread = {
      threadId: '00000000-0000-4000-8000-000000000001',
      runId: '00000000-0000-4000-8000-000000000003',
    };
    const events = [
      { type: 'RUN_STARTED', ...thread },
      { type: 'TOOL_CALL_START', toolCallId: 'c1', toolCallName: 'request_change' },
      // What the model claims is not what the person is shown.
      {
        type: 'TOOL_CALL_ARGS',
        toolCallId: 'c1',
        delta: JSON.stringify({ title: 'Helt ofarligt', operations: [] }),
      },
      { type: 'TOOL_CALL_END', toolCallId: 'c1' },
      {
        type: 'TOOL_CALL_RESULT',
        messageId: 'r1',
        toolCallId: 'c1',
        role: 'tool',
        content: JSON.stringify({ plan: { ref: `plan:${plan}`, status: 'draft' }, changes: [] }),
      },
      { type: 'RUN_FINISHED', ...thread },
    ];
    await route.fulfill({
      status: 200,
      headers: { ...cors, 'Content-Type': 'text/event-stream' },
      body: events.map((e) => `data: ${JSON.stringify(e)}\n\n`).join(''),
    });
  });
  await page.route('**/config.json', (route) =>
    route.fulfill({
      json: {
        oidcAuthority: idp,
        oidcClientId: 'cmdb-web',
        basemap: 'none',
        lotsUrl: lots,
        lotsAuthority: idp.replace('/cmdb/', '/lots/'),
        lotsClientId: 'lots',
        lotsContext: 'cmdb',
      },
    }),
  );
  await page.goto('/');
  await expect(page.locator('cmdb-map .ol-viewport')).toBeVisible();
  await page.getByRole('button', { name: 'Chatt', exact: true }).click();
  const chat = page.getByRole('region', { name: 'Chatt med Lots' });
  await expect(chat.getByText('Du frågar som E2E Planerare')).toBeVisible();

  await chat.getByRole('textbox', { name: 'Din fråga' }).fill('Byt namn på siten');
  await chat.getByRole('textbox', { name: 'Din fråga' }).press('Enter');

  const dialog = chat.getByRole('group', { name: 'Ändringsbegäran' });
  await expect(dialog.getByText('Ändringsbegäran - skriv:')).toBeVisible();
  await expect(dialog.getByText(name)).toBeVisible();
  await expect(dialog.getByText('Helt ofarligt')).toHaveCount(0);
  await expect(dialog.getByText('Ska vi skriva det här?')).toBeVisible();

  const headers = { Authorization: cmdbToken };
  const nameNow = async () =>
    (await (await page.request.get(`/api/sites/${site}`, { headers })).json()).name;
  expect(await nameNow()).not.toBe(name);

  await dialog.getByRole('button', { name: 'Ja' }).click();
  await expect(dialog.getByText('Ändringen är skriven')).toBeVisible();
  expect(await nameNow()).toBe(name);
});
