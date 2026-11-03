/**
 * Ember Guardian demo cloud service.
 *
 * - Account database + player saves: CloudServer/data/db.json
 * - Account API: POST /api/v1/accounts/register | /api/v1/accounts/login
 * - Player save API: GET/PUT /api/v1/players/:id
 * - Static hot-update content: /ServerData/AB/*
 * - Web admin page: GET / (view accounts/saves, add/delete)
 * - No npm dependencies; Node 18+.
 *
 * Start from the project root:
 *   node CloudServer/server.js
 *
 * Env:
 *   PORT=8080
 *   API_TOKEN=dev-token      (set to empty string to disable the bearer check)
 *   CONTENT_ROOT=<project>/ServerData
 *   DATA_DIR=<project>/CloudServer/data
 */

'use strict';

const http = require('node:http');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');

const ROOT = path.resolve(__dirname, '..');
const PORT = Number(process.env.PORT || 8080);
const HOST = process.env.HOST || '0.0.0.0';
const API_TOKEN = process.env.API_TOKEN || 'dev-token';
const CONTENT_ROOT = path.resolve(process.env.CONTENT_ROOT || path.join(ROOT, 'ServerData'));
const DATA_DIR = path.resolve(process.env.DATA_DIR || path.join(__dirname, 'data'));
const DB_FILE = path.join(DATA_DIR, 'db.json');
const LEGACY_PLAYERS_FILE = path.join(DATA_DIR, 'players.json');
const MAX_BODY = 64 * 1024;
const ID_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;
const ACCOUNT_MAX = 32;
const PASSWORD_MAX = 64;

const MIME = {
  '.json': 'application/json; charset=utf-8',
  '.bundle': 'application/octet-stream',
  '.manifest': 'application/octet-stream',
  '.png': 'image/png',
  '.js': 'text/javascript; charset=utf-8',
  '.html': 'text/html; charset=utf-8',
  '': 'application/octet-stream',
};

// ---------------------------------------------------------------------------
// JSON "database"
// ---------------------------------------------------------------------------

function emptyDb() {
  return { schemaVersion: 2, accounts: {}, players: {} };
}

function normalizeDb(parsed) {
  const db = emptyDb();
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return db;
  }
  if (parsed.accounts && typeof parsed.accounts === 'object' && !Array.isArray(parsed.accounts)) {
    db.accounts = parsed.accounts;
  }
  if (parsed.players && typeof parsed.players === 'object' && !Array.isArray(parsed.players)) {
    db.players = parsed.players;
  }
  return db;
}

function loadDb() {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });

    if (fs.existsSync(DB_FILE)) {
      return normalizeDb(JSON.parse(fs.readFileSync(DB_FILE, 'utf8')));
    }

    // First run: migrate the old players-only file into db.json.
    const db = emptyDb();
    if (fs.existsSync(LEGACY_PLAYERS_FILE)) {
      const legacy = JSON.parse(fs.readFileSync(LEGACY_PLAYERS_FILE, 'utf8'));
      if (legacy && typeof legacy === 'object' && !Array.isArray(legacy)) {
        db.players = legacy;
        console.log(`[cloud] migrated ${Object.keys(legacy).length} player save(s) from players.json`);
      }
    }
    return db;
  } catch (error) {
    console.error('[cloud] failed to load db.json:', error.message);
    return emptyDb();
  }
}

const db = loadDb();
let saveChain = Promise.resolve();

function persistDb() {
  // Serialize writes; temp file + rename so a crash never leaves half a JSON file.
  const write = saveChain.then(async () => {
    await fsp.mkdir(DATA_DIR, { recursive: true });
    const temp = `${DB_FILE}.tmp`;
    await fsp.writeFile(temp, JSON.stringify(db, null, 2), 'utf8');
    await fsp.rename(temp, DB_FILE);
  });

  saveChain = write.catch((error) => {
    console.error('[cloud] failed to persist db.json:', error.message);
  });

  return write;
}

// Same derivation as the old client-side PlayerSession.ShortHash:
// sha256(account) first 6 bytes, hex. Keeps existing player saves compatible.
function playerIdOf(account) {
  return 'player-' + crypto.createHash('sha256').update(account, 'utf8').digest('hex').slice(0, 12);
}

// ---------------------------------------------------------------------------
// HTTP helpers
// ---------------------------------------------------------------------------

function sendJson(res, status, payload, extraHeaders = {}) {
  const body = JSON.stringify(payload);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Cache-Control': 'no-store',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': 'Content-Type, Authorization',
    'Access-Control-Allow-Methods': 'GET, PUT, OPTIONS',
    ...extraHeaders,
  });
  res.end(body);
}

function sendText(res, status, text) {
  res.writeHead(status, {
    'Content-Type': 'text/plain; charset=utf-8',
    'Content-Length': Buffer.byteLength(text),
    'Cache-Control': 'no-store',
  });
  res.end(text);
}

function redirect(res, location) {
  res.writeHead(303, { Location: location, 'Cache-Control': 'no-store' });
  res.end();
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

async function readBody(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_BODY) {
      const error = new Error('request body too large');
      error.statusCode = 413;
      throw error;
    }
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString('utf8');
}

async function readForm(req) {
  const body = await readBody(req);
  return Object.fromEntries(new URLSearchParams(body));
}

function readCatalogVersion() {
  try {
    const catalog = JSON.parse(fs.readFileSync(path.join(CONTENT_ROOT, 'AB', 'catalog.json'), 'utf8'));
    return {
      contentVersion: catalog.contentVersion || 'unknown',
      buildNumber: catalog.buildNumber ?? -1,
      builtAtUtc: catalog.builtAtUtc || '',
    };
  } catch {
    return { contentVersion: 'missing', buildNumber: -1, builtAtUtc: '' };
  }
}

function authorized(req) {
  const header = req.headers.authorization || '';
  const token = header.startsWith('Bearer ') ? header.slice(7) : '';
  if (!API_TOKEN) {
    return true;
  }
  const a = Buffer.from(token);
  const b = Buffer.from(API_TOKEN);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

function validateSave(body) {
  if (!body || typeof body !== 'object') {
    return 'body must be a JSON object';
  }
  if (!Number.isInteger(body.hp) || body.hp < 0) {
    return 'hp must be a non-negative integer';
  }
  if (!Number.isInteger(body.maxHp) || body.maxHp < 1 || body.maxHp > 1000000) {
    return 'maxHp must be an integer between 1 and 1000000';
  }
  if (body.hp > body.maxHp) {
    return 'hp cannot exceed maxHp';
  }
  if (typeof body.hotUpdateVersion !== 'string' || body.hotUpdateVersion.length > 80) {
    return 'hotUpdateVersion must be a string <= 80 chars';
  }
  if (body.schemaVersion !== undefined && body.schemaVersion !== 1) {
    return 'unsupported schemaVersion';
  }
  return null;
}

function validateCredentials(rawAccount, rawPassword) {
  const account = String(rawAccount ?? '').trim();
  const password = String(rawPassword ?? '');
  if (!account || account.length > ACCOUNT_MAX) {
    return { error: `账号长度必须为 1-${ACCOUNT_MAX} 个字符` };
  }
  if (!password || password.length > PASSWORD_MAX) {
    return { error: `密码长度必须为 1-${PASSWORD_MAX} 个字符` };
  }
  return { account, password };
}

// ---------------------------------------------------------------------------
// API
// ---------------------------------------------------------------------------

async function handleAccounts(req, res, url) {
  const isRegister = url.pathname === '/api/v1/accounts/register';
  const isLogin = url.pathname === '/api/v1/accounts/login';
  if ((!isRegister && !isLogin) || req.method !== 'POST') {
    return false;
  }

  let body;
  try {
    body = JSON.parse(await readBody(req));
  } catch (error) {
    sendJson(res, error.statusCode || 400, { ok: false, error: error.message });
    return true;
  }

  const credentials = validateCredentials(body.account, body.password);
  if (credentials.error) {
    sendJson(res, 422, { ok: false, error: credentials.error });
    return true;
  }

  const { account, password } = credentials;
  const record = db.accounts[account];

  if (isRegister) {
    if (record) {
      sendJson(res, 409, { ok: false, error: '用户存在' });
      return true;
    }

    // Node is single-threaded and there is no await between the check and the
    // assignment, so two concurrent registrations cannot both succeed.
    db.accounts[account] = {
      password,
      playerId: playerIdOf(account),
      createdAt: new Date().toISOString(),
    };

    await persistDb();
    sendJson(res, 201, {
      ok: true,
      data: { account, playerId: db.accounts[account].playerId },
      serverTimeUtc: new Date().toISOString(),
    });
    return true;
  }

  if (!record || record.password !== password) {
    sendJson(res, 401, { ok: false, error: '账户或密码错误' });
    return true;
  }

  sendJson(res, 200, {
    ok: true,
    data: { account, playerId: record.playerId || playerIdOf(account) },
    serverTimeUtc: new Date().toISOString(),
  });
  return true;
}

async function handleApi(req, res, url) {
  if (req.method === 'OPTIONS') {
    sendJson(res, 204, {});
    return true;
  }

  if (url.pathname === '/api/v1/health' && req.method === 'GET') {
    sendJson(res, 200, {
      ok: true,
      service: 'ember-guardian-cloud',
      time: new Date().toISOString(),
      authRequired: Boolean(API_TOKEN),
      content: readCatalogVersion(),
      accountCount: Object.keys(db.accounts).length,
      playerCount: Object.keys(db.players).length,
    });
    return true;
  }

  if (await handleAccounts(req, res, url)) {
    return true;
  }

  const match = url.pathname.match(/^\/api\/v1\/players\/([^/]+)$/);
  if (!match) {
    return false;
  }

  if (!authorized(req)) {
    sendJson(res, 401, { ok: false, error: 'invalid bearer token' });
    return true;
  }

  let playerId;
  try {
    playerId = decodeURIComponent(match[1]);
  } catch {
    sendJson(res, 400, { ok: false, error: 'invalid player id encoding' });
    return true;
  }

  if (!ID_PATTERN.test(playerId)) {
    sendJson(res, 400, { ok: false, error: 'player id must match ^[A-Za-z0-9._-]{1,64}$' });
    return true;
  }

  if (req.method === 'GET') {
    const save = db.players[playerId];
    if (!save) {
      sendJson(res, 404, { ok: false, error: 'no cloud save for this player' });
      return true;
    }
    sendJson(res, 200, { ok: true, data: save, serverTimeUtc: save.serverTimeUtc });
    return true;
  }

  if (req.method === 'PUT') {
    let body;
    try {
      body = JSON.parse(await readBody(req));
    } catch (error) {
      sendJson(res, error.statusCode || 400, { ok: false, error: error.message });
      return true;
    }

    const validationError = validateSave(body);
    if (validationError) {
      sendJson(res, 422, { ok: false, error: validationError });
      return true;
    }

    // The URL is authoritative; don't allow a body to write another player's row.
    const serverTimeUtc = new Date().toISOString();
    const save = {
      playerId,
      hp: body.hp,
      maxHp: body.maxHp,
      hotUpdateVersion: String(body.hotUpdateVersion),
      clientTimeUtc: typeof body.clientTimeUtc === 'string' ? body.clientTimeUtc.slice(0, 64) : '',
      schemaVersion: 1,
      serverTimeUtc,
      revision: Number.isInteger(db.players[playerId]?.revision) ? db.players[playerId].revision + 1 : 1,
    };

    db.players[playerId] = save;
    await persistDb();

    sendJson(res, 200, { ok: true, data: save, serverTimeUtc });
    return true;
  }

  sendJson(res, 405, { ok: false, error: 'method not allowed' });
  return true;
}

// ---------------------------------------------------------------------------
// Web admin page
// ---------------------------------------------------------------------------

async function handleAdmin(req, res, url) {
  if (req.method !== 'POST') {
    return false;
  }

  let form;
  try {
    form = await readForm(req);
  } catch (error) {
    sendText(res, error.statusCode || 400, error.message);
    return true;
  }

  if (url.pathname === '/admin/register') {
    const credentials = validateCredentials(form.account, form.password);
    if (credentials.error) {
      redirect(res, `/?err=${encodeURIComponent(credentials.error)}`);
      return true;
    }
    if (db.accounts[credentials.account]) {
      redirect(res, `/?err=${encodeURIComponent('用户存在')}`);
      return true;
    }
    db.accounts[credentials.account] = {
      password: credentials.password,
      playerId: playerIdOf(credentials.account),
      createdAt: new Date().toISOString(),
    };
    await persistDb();
    redirect(res, `/?msg=${encodeURIComponent(`已创建账号 ${credentials.account}`)}`);
    return true;
  }

  if (url.pathname === '/admin/account/delete') {
    const account = String(form.account ?? '').trim();
    if (db.accounts[account]) {
      delete db.accounts[account];
      await persistDb();
      redirect(res, `/?msg=${encodeURIComponent(`已删除账号 ${account}（存档保留）`)}`);
    } else {
      redirect(res, `/?err=${encodeURIComponent('账号不存在')}`);
    }
    return true;
  }

  if (url.pathname === '/admin/save/delete') {
    const playerId = String(form.playerId ?? '');
    if (db.players[playerId]) {
      delete db.players[playerId];
      await persistDb();
      redirect(res, `/?msg=${encodeURIComponent(`已删除存档 ${playerId}`)}`);
    } else {
      redirect(res, `/?err=${encodeURIComponent('存档不存在')}`);
    }
    return true;
  }

  if (url.pathname === '/admin/save/hp') {
    const playerId = String(form.playerId ?? '');
    const hp = Number(form.hp);
    const save = db.players[playerId];
    if (!save) {
      redirect(res, `/?err=${encodeURIComponent('存档不存在')}`);
      return true;
    }
    if (!Number.isInteger(hp) || hp < 0 || hp > save.maxHp) {
      redirect(res, `/?err=${encodeURIComponent(`HP 必须是 0-${save.maxHp} 的整数`)}`);
      return true;
    }
    save.hp = hp;
    save.revision = (Number.isInteger(save.revision) ? save.revision : 0) + 1;
    save.serverTimeUtc = new Date().toISOString();
    await persistDb();
    redirect(res, `/?msg=${encodeURIComponent(`已修改 ${playerId} 的 HP 为 ${hp}`)}`);
    return true;
  }

  return false;
}

function renderDashboard(message, error) {
  const accounts = Object.entries(db.accounts).sort(([a], [b]) => a.localeCompare(b));
  const saves = Object.entries(db.players).sort(([a], [b]) => a.localeCompare(b));
  const content = readCatalogVersion();

  const accountRows = accounts.length === 0
    ? '<tr><td colspan="4" class="muted">还没有账号，用下面的表单创建一个。</td></tr>'
    : accounts.map(([account, record]) => `
      <tr>
        <td><code>${escapeHtml(account)}</code></td>
        <td><code>${escapeHtml(record.playerId || '')}</code></td>
        <td class="muted">${escapeHtml(record.createdAt || '')}</td>
        <td>
          <form method="post" action="/admin/account/delete" onsubmit="return confirm('删除账号？存档会保留');">
            <input type="hidden" name="account" value="${escapeHtml(account)}" />
            <button type="submit" class="danger">删除</button>
          </form>
        </td>
      </tr>`).join('');

  const saveRows = saves.length === 0
    ? '<tr><td colspan="6" class="muted">还没有存档，进游戏点一次「上传云端」。</td></tr>'
    : saves.map(([playerId, save]) => `
      <tr>
        <td><code>${escapeHtml(playerId)}</code></td>
        <td><b>${escapeHtml(save.hp)}</b> / ${escapeHtml(save.maxHp)}</td>
        <td>${escapeHtml(save.hotUpdateVersion || '')}</td>
        <td class="muted">${escapeHtml(save.revision ?? '')}</td>
        <td class="muted">${escapeHtml(save.serverTimeUtc || '')}</td>
        <td>
          <form method="post" action="/admin/save/hp" class="inline-form">
            <input type="hidden" name="playerId" value="${escapeHtml(playerId)}" />
            <input type="number" name="hp" min="0" max="${escapeHtml(save.maxHp)}" value="${escapeHtml(save.hp)}" required />
            <button type="submit">改 HP</button>
          </form>
          <form method="post" action="/admin/save/delete" class="inline-form" onsubmit="return confirm('删除这条存档？');">
            <input type="hidden" name="playerId" value="${escapeHtml(playerId)}" />
            <button type="submit" class="danger">删除</button>
          </form>
        </td>
      </tr>`).join('');

  const banner = error
    ? `<div class="banner err">${escapeHtml(error)}</div>`
    : message
      ? `<div class="banner ok">${escapeHtml(message)}</div>`
      : '';

  return `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>Ember Guardian Cloud</title>
<style>
body{margin:0;background:#07101a;color:#dbeaf4;font:14px/1.6 ui-monospace,Consolas,monospace}
main{max-width:1100px;margin:4vh auto;padding:32px;border:1px solid #1e4350;background:#0a1620}
h1{font-size:18px;letter-spacing:3px;color:#6ce3cf;margin:0 0 8px}
h2{font-size:12px;letter-spacing:2px;color:#7fa5b7;margin-top:30px}
code{color:#ffd08a}.ok{color:#66e6b8}.err{color:#ff8a8a}.muted{color:#638294}
a{color:#6ce3cf}
pre{white-space:pre-wrap;background:#06111a;padding:14px;border-left:2px solid #1d6a68}
table{width:100%;border-collapse:collapse;margin-top:10px;font-size:13px}
th,td{text-align:left;padding:7px 8px;border-bottom:1px solid #13313d;vertical-align:middle}
th{color:#7fa5b7;font-weight:normal;letter-spacing:1px}
input{padding:5px 7px;background:#06111a;border:1px solid #1e4350;color:#dbeaf4;font:inherit}
input[type=number]{width:80px}
button{padding:5px 10px;background:#0e2a33;border:1px solid #1e6a68;color:#6ce3cf;font:inherit;cursor:pointer}
button:hover{background:#14424d}
button.danger{border-color:#6a1e1e;color:#ff8a8a}
button.danger:hover{background:#3a1414}
form{display:inline}.inline-form{margin-right:6px}
.row-form{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.banner{margin:12px 0;padding:9px 12px;border-left:3px solid}
.banner.ok{background:#0b2a22;border-color:#2f8f6f;color:#8ef0c8}
.banner.err{background:#2a0f11;border-color:#8f2f2f;color:#ffb3b3}
</style></head><body><main>
<h1>EMBER // GUARDIAN CLOUD</h1>
<div class="muted">JSON database: <code>${escapeHtml(DB_FILE)}</code> · content ${escapeHtml(content.contentVersion)} (build ${escapeHtml(content.buildNumber)})</div>
${banner}
<h2>DATABASE</h2>
<pre>accounts: ${accounts.length}
players:  ${saves.length}
auth:     ${API_TOKEN ? 'bearer token required for /api/v1/players/*' : 'disabled'}</pre>

<h2>NEW ACCOUNT</h2>
<form method="post" action="/admin/register" class="row-form">
  <input name="account" placeholder="账号（1-32）" maxlength="32" required />
  <input name="password" placeholder="密码（1-64）" maxlength="64" required />
  <button type="submit">创建账号</button>
</form>

<h2>ACCOUNTS</h2>
<table>
  <tr><th>账号</th><th>playerId</th><th>创建时间</th><th></th></tr>
  ${accountRows}
</table>

<h2>SAVES</h2>
<table>
  <tr><th>playerId</th><th>HP</th><th>代码版本</th><th>rev</th><th>更新时间</th><th></th></tr>
  ${saveRows}
</table>

<h2>QUICK LINKS</h2>
<div><a href="/api/v1/health">/api/v1/health</a> · <a href="/ServerData/AB/catalog.json">/ServerData/AB/catalog.json</a></div>

<h2>API</h2>
<pre>POST /api/v1/accounts/register   {account,password}
POST /api/v1/accounts/login       {account,password}
GET  /api/v1/health
GET  /api/v1/players/:id          Authorization: Bearer dev-token
PUT  /api/v1/players/:id          Authorization: Bearer dev-token
GET  /ServerData/AB/:file</pre>

<h2>UNITY CONFIG</h2>
<pre>Assets/StreamingAssets/appconfig.json
"cloudBaseUrl": "http://127.0.0.1:${PORT}"
"contentBaseUrl": "http://127.0.0.1:${PORT}/ServerData/AB"</pre>
</main></body></html>`;
}

// ---------------------------------------------------------------------------
// Static content
// ---------------------------------------------------------------------------

async function serveStatic(req, res, url) {
  let pathname;
  try {
    pathname = decodeURIComponent(url.pathname);
  } catch {
    sendJson(res, 400, { ok: false, error: 'bad path encoding' });
    return;
  }

  // Map /ServerData/AB/... to CONTENT_ROOT/AB/... and allow only that subtree.
  const prefix = '/ServerData/';
  if (!pathname.startsWith(prefix)) {
    sendJson(res, 404, { ok: false, error: 'not found' });
    return;
  }

  const relative = pathname.slice(prefix.length);
  const target = path.resolve(CONTENT_ROOT, relative);
  const contentPrefix = CONTENT_ROOT.endsWith(path.sep) ? CONTENT_ROOT : CONTENT_ROOT + path.sep;
  if (target !== CONTENT_ROOT && !target.startsWith(contentPrefix)) {
    sendJson(res, 403, { ok: false, error: 'forbidden' });
    return;
  }

  let stat;
  try {
    stat = await fsp.stat(target);
  } catch {
    sendJson(res, 404, { ok: false, error: 'content not found' });
    return;
  }
  if (!stat.isFile()) {
    sendJson(res, 404, { ok: false, error: 'not a file' });
    return;
  }

  const extension = path.extname(target).toLowerCase();
  res.writeHead(200, {
    'Content-Type': MIME[extension] || 'application/octet-stream',
    'Content-Length': stat.size,
    // Hot-update content must be checked every launch; don't let proxies cache stale catalogs.
    'Cache-Control': 'no-cache, no-store, must-revalidate',
    'Access-Control-Allow-Origin': '*',
  });
  if (req.method === 'HEAD') {
    res.end();
    return;
  }
  fs.createReadStream(target).pipe(res);
}

// ---------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------

const server = http.createServer(async (req, res) => {
  const started = Date.now();
  try {
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

    if (url.pathname === '/' && req.method === 'GET') {
      const html = renderDashboard(url.searchParams.get('msg'), url.searchParams.get('err'));
      res.writeHead(200, {
        'Content-Type': 'text/html; charset=utf-8',
        'Content-Length': Buffer.byteLength(html),
        'Cache-Control': 'no-store',
      });
      res.end(html);
      return;
    }

    if (url.pathname.startsWith('/admin/')) {
      const handled = await handleAdmin(req, res, url);
      if (!handled) {
        sendText(res, 404, 'unknown admin route');
      }
      return;
    }

    if (url.pathname.startsWith('/api/')) {
      const handled = await handleApi(req, res, url);
      if (!handled) {
        sendJson(res, 404, { ok: false, error: 'unknown api route' });
      }
      return;
    }

    if (req.method === 'GET' || req.method === 'HEAD') {
      await serveStatic(req, res, url);
      return;
    }

    sendJson(res, 405, { ok: false, error: 'method not allowed' });
  } catch (error) {
    console.error('[cloud] unhandled error:', error);
    if (!res.headersSent) {
      sendJson(res, 500, { ok: false, error: 'internal server error' });
    } else {
      res.destroy();
    }
  } finally {
    console.log(`[cloud] ${req.method} ${req.url} -> ${res.statusCode} (${Date.now() - started}ms)`);
  }
});

server.listen(PORT, HOST, () => {
  const content = readCatalogVersion();
  console.log('==========================================================');
  console.log(' Ember Guardian cloud service');
  console.log(` URL:      http://127.0.0.1:${PORT}`);
  console.log(` Admin:    http://127.0.0.1:${PORT}/  (no auth, local demo only)`);
  console.log(` Token:    ${API_TOKEN || '(disabled)'}`);
  console.log(` Content:  ${CONTENT_ROOT}`);
  console.log(` Content version: ${content.contentVersion} (build ${content.buildNumber})`);
  console.log(` Database: ${DB_FILE}`);
  console.log(` Accounts: ${Object.keys(db.accounts).length}`);
  console.log(` Saves:    ${Object.keys(db.players).length}`);
  console.log(' Unity appconfig:');
  console.log(`   cloudBaseUrl   = http://127.0.0.1:${PORT}`);
  console.log(`   contentBaseUrl = http://127.0.0.1:${PORT}/ServerData/AB`);
  console.log('==========================================================');
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    console.log('[cloud] shutting down…');
    server.close(() => process.exit(0));
  });
}
