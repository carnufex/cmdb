// Checks that the design tokens in src/styles.scss meet WCAG 2.1 AA in both themes.
// Text needs 4.5:1 against every surface it sits on; status dots, focus rings and borders
// that carry meaning need 3:1 (non-text contrast). Run by scripts/verify.sh.
import { readFileSync } from 'node:fs';

const css = readFileSync(new URL('../src/styles.scss', import.meta.url), 'utf8');

function block(selector) {
  const start = css.indexOf(selector);
  const body = css.slice(css.indexOf('{', start) + 1, css.indexOf('}', start));
  return Object.fromEntries([...body.matchAll(/--([\w-]+):\s*(#[0-9a-f]{6})/gi)].map((m) => [m[1], m[2]]));
}

function luminance(hex) {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function ratio(a, b) {
  const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (l1 + 0.05) / (l2 + 0.05);
}

const surfaces = ['bg', 'surface-1', 'surface-2', 'surface-3'];
const text = ['text', 'text-muted', 'text-faint'];
const nonText = ['focus', 'status-planned', 'status-construction', 'status-in-service', 'status-decommissioning', 'status-removed', 'status-conflict', 'action'];

let failures = 0;
for (const [name, selector] of [['dark', ":root,\n[data-theme='dark']"], ['light', "[data-theme='light']"]]) {
  const t = block(selector);
  const check = (fg, bg, min) => {
    const r = ratio(t[fg], t[bg]);
    if (r < min) {
      failures++;
      console.error(`${name}: --${fg} on --${bg} is ${r.toFixed(2)}:1, needs ${min}:1`);
    }
  };
  for (const s of surfaces) {
    text.forEach((fg) => check(fg, s, 4.5));
    nonText.forEach((fg) => check(fg, s, 3));
  }
  check('action-text', 'action', 4.5);
}

if (failures > 0) {
  process.exit(1);
}
console.log('Design tokens meet WCAG 2.1 AA in dark and light themes.');
