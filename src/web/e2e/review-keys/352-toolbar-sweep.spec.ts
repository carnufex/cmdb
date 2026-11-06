import { expect, Page, test } from '@playwright/test';

/**
 * Review key for #352: the toolbar fits at every width from 1280 to 1920 px in steps of 20 — no control overlaps
 * another, each is seen whole inside the window, the search box is at least 200 px — with and without the chat button
 * and with and without an active plan, and a plan name of 60 characters changes none of that. Runs with the e2e stack:
 * `scripts/e2e.sh -- review-keys/352-toolbar-sweep.spec.ts`.
 */

const idp = process.env.E2E_IDP ?? 'http://127.0.0.1:8790/application/o/cmdb/';
const SEARCH_MIN = 200;
const WIDTHS = Array.from({ length: 33 }, (_, i) => 1280 + i * 20);
const LONG_NAME = 'Kapacitetsutbyggnad norra stamnätet etapp två med reservväg'.padEnd(60, '.');

async function open(page: Page, path: string, chat: boolean): Promise<void> {
  await page.route('**/config.json', (route) =>
    route.fulfill({
      json: {
        oidcAuthority: idp,
        oidcClientId: 'cmdb-web',
        basemap: 'none',
        voiceAgentId: '',
        voiceNocAgentId: '',
        ...(chat
          ? { lotsUrl: 'http://127.0.0.1:8791', lotsAuthority: idp, lotsClientId: 'lots-web' }
          : {}),
      },
    }),
  );
  await page.goto(path);
  await expect(page.getByRole('combobox', { name: 'Sök eller kör en åtgärd' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^Nyheter/ })).toBeVisible();
  // The signed-in person's name and scope arrive after the first paint and take room in the toolbar.
  await expect(page.locator('header.topbar .scope')).toBeVisible();
  if (chat) await expect(page.getByRole('button', { name: 'Chatt' })).toBeAttached();
}

/** Problems with the toolbar as it is drawn now: overlaps, controls outside or hidden, a narrow search box. */
function problems(page: Page, searchMin: number): Promise<string[]> {
  return page.locator('header.topbar').evaluate((bar, min) => {
    const picked = [
      ...bar.querySelectorAll<HTMLElement>(
        'a, button, .synthetic, .scope, .user, cmdb-search .search',
      ),
    ].filter((e) => !e.closest('cmdb-search .search') || e.matches('cmdb-search .search'));
    const visible = picked.filter((e) => {
      const r = e.getBoundingClientRect();
      const s = getComputedStyle(e);
      return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
    });
    const leaves = visible.filter(
      (e) => e.matches('cmdb-search .search') || !visible.some((o) => o !== e && e.contains(o)),
    );
    const out: string[] = [];
    const boxes = leaves.map((e) => {
      const r = e.getBoundingClientRect();
      const name = (e.getAttribute('aria-label') ?? e.textContent ?? e.className)
        .replace(/\s+/g, ' ')
        .trim()
        .slice(0, 30);
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      if (r.left < -0.5 || r.right > window.innerWidth + 0.5)
        out.push(`${name} lies outside the window`);
      if (!hit || !(hit === e || e.contains(hit))) out.push(`${name} is covered or clipped`);
      return { name, r };
    });
    for (let i = 0; i < boxes.length; i++)
      for (let j = i + 1; j < boxes.length; j++) {
        const a = boxes[i].r;
        const b = boxes[j].r;
        const w = Math.min(a.right, b.right) - Math.max(a.left, b.left);
        const h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        if (w > 0.5 && h > 0.5) out.push(`${boxes[i].name} overlaps ${boxes[j].name}`);
      }
    const search = bar.querySelector('cmdb-search .search')?.getBoundingClientRect().width ?? 0;
    if (search < min) out.push(`the search box is ${Math.round(search)} px`);
    return out;
  }, searchMin);
}

/** Every width, narrowing and then widening again, so a layout that depends on the way there is caught too. */
async function sweep(page: Page): Promise<string[]> {
  const found: string[] = [];
  for (const width of [...WIDTHS.slice().reverse(), ...WIDTHS]) {
    await page.setViewportSize({ width, height: Math.round(width * 0.625) });
    await page.evaluate(
      () =>
        new Promise((r) =>
          requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(r, 60))),
        ),
    );
    for (const p of await problems(page, SEARCH_MIN)) found.push(`${width} px: ${p}`);
  }
  return [...new Set(found)];
}

const plans: { normal: number; long: number } = { normal: 0, long: 0 };

async function makePlan(page: Page, name: string): Promise<number> {
  await page.goto('/?tool=plans&size=half');
  await page.getByRole('button', { name: 'Ny plan' }).first().click();
  await page.getByRole('textbox', { name: 'Planens namn' }).fill(name);
  await page.getByRole('button', { name: 'Skapa och visa' }).click();
  await expect(page).toHaveURL(/plan=\d+/);
  return Number(new URL(page.url()).searchParams.get('plan'));
}

test.beforeAll(async ({ browser }) => {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
  await open(page, '/', false);
  plans.normal = await makePlan(page, 'Review key 352');
  plans.long = await makePlan(page, LONG_NAME);
  await page.close();
});

for (const chat of [false, true]) {
  for (const plan of [false, true]) {
    test(`the toolbar fits from 1280 to 1920 px ${chat ? 'with' : 'without'} chat ${plan ? 'in a plan' : 'in production'}`, async ({
      page,
    }) => {
      await page.setViewportSize({ width: 1920, height: 1200 });
      await open(page, plan ? `/?plan=${plans.normal}` : '/', chat);
      if (plan) await expect(page.locator('.plan-chip')).toBeVisible();
      expect(await sweep(page)).toEqual([]);
    });
  }
}

test('a plan name of 60 characters changes nothing, with chat', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1200 });
  await open(page, `/?plan=${plans.long}`, true);
  await expect(page.locator('.plan-chip')).toBeVisible();
  expect(await sweep(page)).toEqual([]);
});
