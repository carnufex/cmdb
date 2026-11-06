import { expect, Page, test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Review key for #332: every control in the toolbar fits — none overlaps another, each is seen whole inside the window —
 * and the search box stays usable (at least 200 px), at the smallest width the app supports (1280 px, #72) and at
 * 1400 px, with and without the chat button and with and without an active plan. Runs with the e2e stack:
 * `scripts/e2e.sh -- review-keys/332-toolbar.spec.ts`. E2E_SHOTS=<dir> also saves the toolbar of every case.
 */

const idp = process.env.E2E_IDP ?? 'http://127.0.0.1:8790/application/o/cmdb/';
const shots = process.env.E2E_SHOTS;
const SEARCH_MIN = 200;

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
  if (chat) await expect(page.getByRole('button', { name: 'Chatt' })).toBeAttached();
}

interface Control {
  name: string;
  left: number;
  top: number;
  right: number;
  bottom: number;
  seen: boolean;
}

/** The toolbar's controls as boxes: the innermost ones, and the search box as one control. */
function measure(page: Page): Promise<{ controls: Control[]; search: number; width: number }> {
  return page.locator('header.topbar').evaluate((bar) => {
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
    const controls = leaves.map((e) => {
      const r = e.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      const name = (e.getAttribute('aria-label') ?? e.textContent ?? e.className)
        .replace(/\s+/g, ' ')
        .trim();
      return {
        name: name.slice(0, 40) || e.tagName.toLowerCase(),
        left: r.left,
        top: r.top,
        right: r.right,
        bottom: r.bottom,
        seen: !!hit && (hit === e || e.contains(hit)),
      };
    });
    const search = bar.querySelector('cmdb-search .search')?.getBoundingClientRect().width ?? 0;
    return { controls, search, width: window.innerWidth };
  });
}

function problems(m: { controls: Control[]; search: number; width: number }): string[] {
  const out: string[] = [];
  const c = m.controls;
  for (let i = 0; i < c.length; i++) {
    const a = c[i];
    if (a.left < -0.5 || a.right > m.width + 0.5) out.push(`${a.name} lies outside the window`);
    if (!a.seen) out.push(`${a.name} is covered or clipped`);
    for (let j = i + 1; j < c.length; j++) {
      const b = c[j];
      const w = Math.min(a.right, b.right) - Math.max(a.left, b.left);
      const h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
      if (w > 0.5 && h > 0.5) out.push(`${a.name} overlaps ${b.name} by ${w.toFixed(1)} px`);
    }
  }
  if (m.search < SEARCH_MIN)
    out.push(`the search box is ${m.search.toFixed(0)} px, under ${SEARCH_MIN}`);
  return out;
}

let planId = 0;

test.beforeAll(async ({ browser }) => {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await open(page, '/?tool=plans&size=half', false);
  await page.getByRole('button', { name: 'Ny plan' }).first().click();
  await page.getByRole('textbox', { name: 'Planens namn' }).fill('Review key 332');
  await page.getByRole('button', { name: 'Skapa och visa' }).click();
  await expect(page).toHaveURL(/plan=\d+/);
  planId = Number(new URL(page.url()).searchParams.get('plan'));
  await page.close();
});

for (const [width, height] of [
  [1280, 800],
  [1400, 900],
] as const) {
  for (const chat of [false, true]) {
    for (const plan of [false, true]) {
      const name = `${width} px, ${chat ? 'with' : 'without'} chat, ${plan ? 'in a plan' : 'in production'}`;
      test(`the toolbar fits at ${name}`, async ({ page }) => {
        await page.setViewportSize({ width, height });
        await open(page, plan ? `/?plan=${planId}` : '/', chat);
        if (plan) await expect(page.locator('.plan-chip')).toBeVisible();
        const m = await measure(page);
        if (shots) {
          mkdirSync(shots, { recursive: true });
          await page.locator('header.topbar').screenshot({
            path: join(
              shots,
              `332-${width}-${chat ? 'chat' : 'nochat'}-${plan ? 'plan' : 'prod'}.png`,
            ),
          });
        }
        expect(
          problems(m),
          `${m.controls.length} controls, search ${m.search.toFixed(0)} px`,
        ).toEqual([]);
      });
    }
  }
}
