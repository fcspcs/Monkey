// Testgeschirr - hostet den ECHTEN worker.js hinter einem lokalen HTTP-Server,
// damit die .NET-Tests (src/Monkey.Tests) die komplette PC-Seite gegen den
// echten Worker-Code fahren koennen. Telegram bleibt Attrappe und wird unter
// /__harness/telegram zum Nachsehen angeboten; alles andere ist echt.
//
// Nur fuer Tests. Diese Datei wird weder deployt noch eingebettet.

import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('./worker.js', import.meta.url), 'utf8');
const moduleUrl = `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
const worker = (await import(moduleUrl)).default;

class MemoryKv {
  constructor() { this.map = new Map(); }

  async get(key, type) {
    const entry = this.map.get(key);
    if (!entry || (entry.expiresAt && Date.now() > entry.expiresAt)) return null;
    return type === 'json' ? JSON.parse(entry.value) : entry.value;
  }

  async put(key, value, { expirationTtl } = {}) {
    this.map.set(key, {
      value,
      expiresAt: expirationTtl ? Date.now() + expirationTtl * 1000 : null,
    });
  }

  async delete(key) { this.map.delete(key); }

  async list({ prefix = '' } = {}) {
    const keys = [...this.map.entries()]
      .filter(([key, entry]) => key.startsWith(prefix) && !(entry.expiresAt && Date.now() > entry.expiresAt))
      .map(([name]) => ({ name }))
      .sort((a, b) => a.name.localeCompare(b.name));
    return { keys };
  }
}

// Muss zu WorkerHarness in src/Monkey.Tests passen.
const env = {
  KV: new MemoryKv(),
  SYNC_SECRET: 'harness-sync-secret-0123456789abcdef',
  MONKEY_BOT_TOKEN: `123456:${'A'.repeat(35)}`,
  FRIEND_BOT_TOKEN: `654321:${'B'.repeat(35)}`,
  MONKEY_WEBHOOK_SECRET: 'C'.repeat(32),
  FRIEND_WEBHOOK_SECRET: 'D'.repeat(32),
};

// Ausgehende Telegram-Aufrufe des Workers: aufzeichnen statt senden.
const telegramCalls = [];
globalThis.fetch = async (url, init) => {
  telegramCalls.push({ url: String(url), body: init?.body ? JSON.parse(init.body) : null });
  return new Response(JSON.stringify({ ok: true, result: {} }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
};

const server = createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && req.url === '/__harness/telegram') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify(telegramCalls));
      return;
    }

    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    const body = Buffer.concat(chunks);

    // Nur die Kopfzeilen weiterreichen, die der Worker tatsaechlich liest -
    // Node-Transportkoepfe wie connection/host haben in einem Fetch-Request
    // nichts verloren.
    const headers = {};
    for (const name of ['authorization', 'content-type', 'x-telegram-bot-api-secret-token'])
      if (req.headers[name]) headers[name] = req.headers[name];

    const request = new Request(`https://relay.example${req.url}`, {
      method: req.method,
      headers,
      body: req.method === 'GET' || req.method === 'HEAD' ? undefined : body,
    });

    const response = await worker.fetch(request, env);
    res.writeHead(response.status, Object.fromEntries(response.headers));
    res.end(Buffer.from(await response.arrayBuffer()));
  } catch (err) {
    res.writeHead(500, { 'content-type': 'text/plain' });
    res.end(String(err));
  }
});

server.listen(0, '127.0.0.1', () => {
  // Die Tests warten auf genau diese Zeile.
  console.log(`HARNESS_PORT ${server.address().port}`);
});
