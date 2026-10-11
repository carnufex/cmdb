// A stand-in OpenID provider for the e2e suite (#288): discovery, a signing key, an authorize endpoint that signs the
// user in at once, and a token endpoint. Tokens are real RS256 JWTs, so the API validates them as it validates
// Authentik's (Auth:Authority points here). The user is a synthetic planner with the cmdb-full role. Never deploy this.
import { createServer } from 'node:http';
import { createSign, generateKeyPairSync, randomUUID } from 'node:crypto';

const port = Number(process.env.E2E_IDP_PORT ?? 8790);
const base = `http://127.0.0.1:${port}`;
const issuer = `${base}/application/o/cmdb/`;
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const kid = randomUUID();
const jwk = { ...publicKey.export({ format: 'jwk' }), kid, alg: 'RS256', use: 'sig' };
const nonces = new Map();

const b64 = (value) =>
  Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)).toString('base64url');

function jwt(claims) {
  const head = b64({ alg: 'RS256', typ: 'JWT', kid });
  const body = b64(claims);
  const signature = createSign('RSA-SHA256')
    .update(`${head}.${body}`)
    .sign(privateKey)
    .toString('base64url');
  return `${head}.${body}.${signature}`;
}

/** The synthetic planner every sign-in is. */
function claims(audience, extra = {}) {
  const now = Math.floor(Date.now() / 1000);
  return {
    iss: issuer,
    aud: audience,
    sub: 'e2e-planerare',
    preferred_username: 'e2e-planerare',
    name: 'E2E Planerare',
    email: 'e2e@example.invalid',
    groups: ['cmdb-full'],
    azp: 'cmdb-web',
    iat: now,
    exp: now + 3600,
    ...extra,
  };
}

function send(res, status, body, headers = {}) {
  res.writeHead(status, {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': '*',
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    ...headers,
  });
  res.end(body === undefined ? undefined : JSON.stringify(body));
}

createServer((req, res) => {
  const url = new URL(req.url, base);
  if (req.method === 'OPTIONS') {
    return send(res, 204);
  }
  if (url.pathname === '/application/o/cmdb/.well-known/openid-configuration') {
    return send(res, 200, {
      issuer,
      authorization_endpoint: `${base}/authorize`,
      token_endpoint: `${base}/token`,
      userinfo_endpoint: `${base}/userinfo`,
      end_session_endpoint: `${base}/logout`,
      jwks_uri: `${base}/jwks`,
      response_types_supported: ['code'],
      subject_types_supported: ['public'],
      id_token_signing_alg_values_supported: ['RS256'],
      code_challenge_methods_supported: ['S256'],
    });
  }
  if (url.pathname === '/jwks') {
    return send(res, 200, { keys: [jwk] });
  }
  if (url.pathname === '/authorize') {
    const code = randomUUID();
    nonces.set(code, url.searchParams.get('nonce'));
    const back = new URL(url.searchParams.get('redirect_uri'));
    back.searchParams.set('code', code);
    back.searchParams.set('state', url.searchParams.get('state') ?? '');
    return send(res, 302, undefined, { Location: back.toString() });
  }
  if (url.pathname === '/userinfo') {
    return send(res, 200, { sub: 'e2e-planerare', name: 'E2E Planerare' });
  }
  if (url.pathname === '/logout') {
    return send(res, 302, undefined, {
      Location: url.searchParams.get('post_logout_redirect_uri') ?? '/',
    });
  }
  if (url.pathname === '/token' && req.method === 'POST') {
    let raw = '';
    req.on('data', (chunk) => (raw += chunk));
    req.on('end', () => {
      const form = new URLSearchParams(raw);
      const nonce = nonces.get(form.get('code')) ?? undefined;
      send(res, 200, {
        access_token: jwt(claims('cmdb-web')),
        id_token: jwt(claims('cmdb-web', { nonce })),
        refresh_token: randomUUID(),
        token_type: 'Bearer',
        expires_in: 3600,
        scope: 'openid profile email offline_access',
      });
    });
    return;
  }
  send(res, 404, { error: 'not found' });
}).listen(port, '127.0.0.1', () => console.log(`e2e idp on ${issuer}`));
