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
