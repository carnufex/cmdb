import type { Token, Tokens } from 'marked';

/** A model's answer as data. It is drawn by our own template, so nothing a model writes ever reaches the page as HTML. */
export type Inline =
  | { kind: 'text'; text: string }
  | { kind: 'strong' | 'em' | 'del'; children: Inline[] }
  | { kind: 'code'; text: string }
  | { kind: 'cite'; source: number }
  | { kind: 'object'; ref: string; children: Inline[] };

export type Block =
  | { kind: 'p'; inline: Inline[] }
  | { kind: 'h'; inline: Inline[] }
  | { kind: 'ul' | 'ol'; items: Block[][] }
  | { kind: 'code'; text: string }
  | { kind: 'quote'; blocks: Block[] }
  | { kind: 'table'; head: Inline[][]; rows: Inline[][][] }
  | { kind: 'hr' };

export type Lexer = (text: string) => Token[];

/** The links a tool's answer gives, into this app: an object's panel. */
const OBJECT_LINK =
  /[?&]p=((?:site|equipment|cable|service|circuit|route-segment|service-point):\d+)/;
const CITATION = /\[t(\d+)\]/g;

const ENTITIES: Readonly<Record<string, string>> = {
  '&amp;': '&',
  '&lt;': '<',
  '&gt;': '>',
  '&quot;': '"',
  '&#39;': "'",
};

const decode = (text: string) => text.replace(/&(?:amp|lt|gt|quot|#39);/g, (e) => ENTITIES[e]);

/** Text with the citations of Lots ([t1]) picked out. */
function text(value: string): Inline[] {
  const out: Inline[] = [];
  let at = 0;
  for (const match of value.matchAll(CITATION)) {
    if (match.index > at) {
      out.push({ kind: 'text', text: value.slice(at, match.index) });
    }
    out.push({ kind: 'cite', source: Number(match[1]) });
    at = match.index + match[0].length;
  }
  if (at < value.length) {
    out.push({ kind: 'text', text: value.slice(at) });
  }
  return out;
}

function link(token: Tokens.Link, origin: string): Inline[] {
  const label = inlines(token.tokens ?? [], origin);
  try {
    const target = new URL(token.href, origin);
    const object = OBJECT_LINK.exec(target.search);
    if (object && target.origin === origin) {
      return [{ kind: 'object', ref: object[1], children: label }];
    }
  } catch {
    // Not a link at all.
  }
  // Nothing outside this app is a link to click: a model can be talked into writing anything. The address stays readable.
  const shown =
    token.text === token.href ? [] : [{ kind: 'text' as const, text: ` (${token.href})` }];
  return [...label, ...shown];
}

function inlines(tokens: readonly Token[], origin: string): Inline[] {
  return tokens.flatMap((token): Inline[] => {
    switch (token.type) {
      case 'strong':
      case 'em':
      case 'del':
        return [{ kind: token.type, children: inlines((token as Tokens.Strong).tokens, origin) }];
      case 'codespan':
        return [{ kind: 'code', text: decode((token as Tokens.Codespan).text) }];
      case 'link':
        return link(token as Tokens.Link, origin);
      case 'image':
        return [{ kind: 'text', text: (token as Tokens.Image).text }];
      case 'br':
        return [{ kind: 'text', text: '\n' }];
      case 'text': {
        const nested = (token as Tokens.Text).tokens;
        return nested?.length ? inlines(nested, origin) : text(decode((token as Tokens.Text).text));
      }
      default:
        return text(decode('text' in token ? String(token['text']) : token.raw));
    }
  });
}

function blocks(tokens: readonly Token[], origin: string): Block[] {
  return tokens.flatMap((token): Block[] => {
    switch (token.type) {
      case 'space':
        return [];
      case 'heading':
        return [{ kind: 'h', inline: inlines((token as Tokens.Heading).tokens, origin) }];
      case 'paragraph':
        return [{ kind: 'p', inline: inlines((token as Tokens.Paragraph).tokens, origin) }];
      case 'text': {
        const nested = (token as Tokens.Text).tokens;
        return [
          {
            kind: 'p',
            inline: nested ? inlines(nested, origin) : text(decode((token as Tokens.Text).text)),
          },
        ];
      }
      case 'list': {
        const list = token as Tokens.List;
        return [
          {
            kind: list.ordered ? 'ol' : 'ul',
            items: list.items.map((item) => blocks(item.tokens, origin)),
          },
        ];
      }
      case 'code':
        return [{ kind: 'code', text: (token as Tokens.Code).text }];
      case 'blockquote':
        return [{ kind: 'quote', blocks: blocks((token as Tokens.Blockquote).tokens, origin) }];
      case 'table': {
        const table = token as Tokens.Table;
        return [
          {
            kind: 'table',
            head: table.header.map((c) => inlines(c.tokens, origin)),
            rows: table.rows.map((r) => r.map((c) => inlines(c.tokens, origin))),
          },
        ];
      }
      case 'hr':
        return [{ kind: 'hr' }];
      default:
        // Also HTML a model wrote: shown as the text it is.
        return token.raw.trim() ? [{ kind: 'p', inline: text(token.raw.trim()) }] : [];
    }
  });
}

/** Without the markdown parser, which loads with the first question, the answer is plain text meanwhile. */
export function parseAnswer(lexer: Lexer | null, answer: string, origin: string): Block[] {
  return lexer ? blocks(lexer(answer), origin) : [{ kind: 'p', inline: text(answer) }];
}

/** What the person gets on copying an answer: without the citations, which mean nothing elsewhere. */
export function withoutCitations(answer: string): string {
  return answer.replace(/ ?\[t\d+\]/g, '');
}
