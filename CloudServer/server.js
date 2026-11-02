/**
 * Ember Guardian demo cloud service.
 *
 * - Player save API: GET/PUT /api/v1/players/:id
 * - Static hot-update content: /ServerData/AB/*
 * - No npm dependencies; Node 18+.
 *
 * Start from the project root:
 *   node CloudServer/server.js
 *
 * Env:
 *   PORT=8080
 *   API_TOKEN=dev-token
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
const DATA_FILE = path.join(DATA_DIR, 'players.json');
const MAX_BODY = 64 * 1024;
const ID_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;

const MIME = {
  '.json': 'application/json; charset=utf-8',
  '.bundle': 'application/octet-stream',
  '.manifest': 'application/octet-stream',
  '.png': 'image/png',
  '.js': 'text/javascript; charset=utf-8',
  '.html': 'text/html; charset=utf-8',
  '': 'application/octet-stream',
};

let players = loadPlayers();
let saveChain = Promise.resolve();

function loadPlayers() {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    if (!fs.existsSync(DATA_FILE)) {
      return {};
    }
    const parsed = JSON.parse(fs.readFileSync(DATA_FILE, 'utf8'));
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch (error) {
    console.error('[cloud] failed to load players.json:', error.message);
    return {};
  }
}

function persistPlayers() {
  // Serialize writes so two concurrent PUTs cannot interleave a partial file.
  // Return the specific write promise so the caller can answer 500 if the disk write fails,
  // while keeping the shared chain alive for the next request.
  const write = saveChain.then(async () => {
    await fsp.mkdir(DATA_DIR, { recursive: true });
    const temp = `${DATA_FILE}.tmp`;
    await fsp.writeFile(temp, JSON.stringify(players, null, 2), 'utf8');
    await fsp.rename(temp, DATA_FILE);
  });

  saveChain = write.catch((error) => {
    console.error('[cloud] failed to persist players:', error.message);
  });

  return write;
}

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
      playerCount: Object.keys(players).length,
    });
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
    const save = players[playerId];
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
      revision: Number.isInteger(players[playerId]?.revision) ? players[playerId].revision + 1 : 1,
    };

    players[playerId] = save;
    await persistPlayers();

    sendJson(res, 200, { ok: true, data: save, serverTimeUtc });
    return true;
  }

  sendJson(res, 405, { ok: false, error: 'method not allowed' });
  return true;
}

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

const DASHBOARD = `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>Ember Guardian Cloud</title>
<style>
body{margin:0;background:#07101a;color:#dbeaf4;font:14px/1.6 ui-monospace,Consolas,monospace}
main{max-width:860px;margin:8vh auto;padding:32px;border:1px solid #1e4350;background:#0a1620}
h1{font-size:18px;letter-spacing:3px;color:#6ce3cf;margin:0 0 8px}
h2{font-size:12px;letter-spacing:2px;color:#7fa5b7;margin-top:26px}
code{color:#ffd08a}.ok{color:#66e6b8}.muted{color:#638294}
a{color:#6ce3cf}
pre{white-space:pre-wrap;background:#06111a;padding:14px;border-left:2px solid #1d6a68}
</style></head><body><main>
<h1>EMBER // GUARDIAN CLOUD</h1>
<div class="muted">Local demo server. Static AB files + JSON player saves.</div>
<h2>HEALTH</h2><pre id="health">loading…</pre>
<h2>QUICK LINKS</h2>
<div><a href="/ServerData/AB/catalog.json">/ServerData/AB/catalog.json</a> · <a href="/ServerData/AB/build-info.json">/ServerData/AB/build-info.json</a></div>
<h2>API</h2><pre>GET  /api/v1/health
GET  /api/v1/players/:id       Authorization: Bearer dev-token
PUT  /api/v1/players/:id       Authorization: Bearer dev-token
GET  /ServerData/AB/:file</pre>
<h2>UNITY CONFIG</h2><pre>Assets/StreamingAssets/appconfig.json
"cloudBaseUrl": "http://127.0.0.1:8080"
"contentBaseUrl": "http://127.0.0.1:8080/ServerData/AB"</pre>
</main><script>
fetch('/api/v1/health').then(r=>r.json()).then(j=>{document.getElementById('health').textContent=JSON.stringify(j,null,2)})
.catch(e=>{document.getElementById('health').textContent=String(e)});
</script></body></html>`;

const server = http.createServer(async (req, res) => {
  const started = Date.now();
  try {
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

    if (url.pathname === '/' && req.method === 'GET') {
      res.writeHead(200, {
        'Content-Type': 'text/html; charset=utf-8',
        'Cache-Control': 'no-store',
      });
      res.end(DASHBOARD);
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
  console.log(` Token:    ${API_TOKEN}`);
  console.log(` Content:  ${CONTENT_ROOT}`);
  console.log(` Content version: ${content.contentVersion} (build ${content.buildNumber})`);
  console.log(` Data:     ${DATA_FILE}`);
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
