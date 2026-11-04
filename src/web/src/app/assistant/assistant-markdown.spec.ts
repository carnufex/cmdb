import { marked } from 'marked';
import { Lexer, parseAnswer, withoutCitations } from './assistant-markdown';

const lexer: Lexer = (text) => marked.lexer(text, { gfm: true, breaks: true });
const origin = 'https://cmdb.example.org';
const parse = (text: string) => parseAnswer(lexer, text, origin);

describe('parseAnswer', () => {
  it('reads what the model writes: bold, lists and numbered lists', () => {
    expect(parse('**Sajter:** text\n\n* en\n* två\n\n1. ett')).toEqual([
      {
        kind: 'p',
        inline: [
          { kind: 'strong', children: [{ kind: 'text', text: 'Sajter:' }] },
          { kind: 'text', text: ' text' },
        ],
      },
      {
        kind: 'ul',
        items: [
          [{ kind: 'p', inline: [{ kind: 'text', text: 'en' }] }],
          [{ kind: 'p', inline: [{ kind: 'text', text: 'två' }] }],
        ],
      },
      { kind: 'ol', items: [[{ kind: 'p', inline: [{ kind: 'text', text: 'ett' }] }]] },
    ]);
  });

  it('picks out the citations of Lots, also two in a row', () => {
    expect(parse('En brunn [t1][t2].')).toEqual([
      {
        kind: 'p',
        inline: [
          { kind: 'text', text: 'En brunn ' },
          { kind: 'cite', source: 1 },
          { kind: 'cite', source: 2 },
          { kind: 'text', text: '.' },
        ],
      },
    ]);
  });

  it('leaves code as it is, citations and all', () => {
    expect(parse('`[t1]` och `a<b`')).toEqual([
      {
        kind: 'p',
        inline: [
          { kind: 'code', text: '[t1]' },
          { kind: 'text', text: ' och ' },
          { kind: 'code', text: 'a<b' },
        ],
      },
    ]);
  });

  it('turns a link to an object in this app into that object', () => {
    expect(parse(`[AGG-1](${origin}/?p=site:5)`)).toEqual([
      {
        kind: 'p',
        inline: [{ kind: 'object', ref: 'site:5', children: [{ kind: 'text', text: 'AGG-1' }] }],
      },
    ]);
  });

  it('makes no link of anything else, and shows the address', () => {
    expect(parse('[Klicka här](https://evil.example/x)')).toEqual([
      {
        kind: 'p',
        inline: [
          { kind: 'text', text: 'Klicka här' },
          { kind: 'text', text: ' (https://evil.example/x)' },
        ],
      },
    ]);
    const other = JSON.stringify(
      parse('[a](https://evil.example/?p=site:5) [b](javascript:alert(1))'),
    );
    expect(other).not.toContain('"object"');
  });

  it('shows HTML and images as the text they are', () => {
    const html = JSON.stringify(
      parse('<img src=x onerror=alert(1)>\n\n![alt](https://evil.example/x.png)'),
    );
    expect(html).toContain('onerror');
    expect(html).toContain('"kind":"text"');
    expect(html).not.toContain('"src"');
    expect(html).not.toContain('evil.example/x.png');
  });

  it('keeps the characters a parser escapes', () => {
    expect(parse('a & b < c')).toEqual([
      { kind: 'p', inline: [{ kind: 'text', text: 'a & b < c' }] },
    ]);
  });

  it('reads a table', () => {
    const [table] = parse('| A | B |\n|---|---|\n| 1 | 2 |');
    expect(table.kind).toBe('table');
  });

  it('is plain text until the parser has loaded', () => {
    expect(parseAnswer(null, '**x** [t1]', origin)).toEqual([
      {
        kind: 'p',
        inline: [
          { kind: 'text', text: '**x** ' },
          { kind: 'cite', source: 1 },
        ],
      },
    ]);
  });
});

describe('withoutCitations', () => {
  it('leaves them out of what is copied', () => {
    expect(withoutCitations('Brunnen [t1][t2] är i drift [t3].')).toBe('Brunnen är i drift.');
  });
});
