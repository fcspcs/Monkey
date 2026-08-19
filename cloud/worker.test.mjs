// Tests fuer den Telegram-Relay-Worker. Laufen ohne jede Abhaengigkeit mit
// Nodes eingebautem Runner:
//
//   node --test cloud/
//
// Der Worker wird als Modul geladen und ueber seine echte HTTP-Schnittstelle
// angesprochen; KV, Telegram und die Uhr sind durch Fakes ersetzt.

import { test, describe, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('./worker.js', import.meta.url), 'utf8');
const moduleUrl = `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
const worker = (await import(moduleUrl)).default;

// ------------------------------------------------------------------- Fakes

/** KV-Namespace-Fake mit den vom Worker genutzten Faehigkeiten inkl. TTL. */
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

const SECRET = 'sync-secret-with-at-least-thirty-two-characters';
const MONKEY_TOKEN = `123456:${'A'.repeat(35)}`;
const FRIEND_TOKEN = `654321:${'B'.repeat(35)}`;
const MONKEY_HOOK = 'C'.repeat(32);
const FRIEND_HOOK = 'D'.repeat(32);

function makeEnv(overrides = {}) {
  return {
    KV: new MemoryKv(),
    SYNC_SECRET: SECRET,
    MONKEY_BOT_TOKEN: MONKEY_TOKEN,
    FRIEND_BOT_TOKEN: FRIEND_TOKEN,
    MONKEY_WEBHOOK_SECRET: MONKEY_HOOK,
    FRIEND_WEBHOOK_SECRET: FRIEND_HOOK,
    ...overrides,
  };
}

// Ausgehende Telegram-Aufrufe des Workers werden aufgezeichnet statt gesendet.
let telegramCalls = [];
let telegramResponder = () => ({ ok: true, result: {} });
globalThis.fetch = async (url, init) => {
  telegramCalls.push({
    url: String(url),
    body: init?.body ? JSON.parse(init.body) : null,
  });
  return new Response(JSON.stringify(telegramResponder()), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
};

// Feste, verstellbare Uhr - die Projektion bei ausgeschaltetem PC haengt davon ab.
const realNow = Date.now;
let now = Date.UTC(2026, 7, 16, 12, 0, 0);

beforeEach(() => {
  now = Date.UTC(2026, 7, 16, 12, 0, 0);
  Date.now = () => now;
  telegramCalls = [];
  telegramResponder = () => ({ ok: true, result: {} });
});

afterEach(() => { Date.now = realNow; });

// ----------------------------------------------------------------- Helfer

function pcRequest(path, { method = 'POST', body = {}, secret = SECRET, raw } = {}) {
  return new Request(`https://relay.example${path}`, {
    method,
    headers: { authorization: `Bearer ${secret}`, 'content-type': 'application/json' },
    body: method === 'GET' ? undefined : raw ?? JSON.stringify(body),
  });
}

const send = (env, path, options) => worker.fetch(pcRequest(path, options), env);

function update(chatId, text, type = 'private') {
  return { message: { text, chat: { id: chatId, type } } };
}

function webhook(env, role, body, secret) {
  return worker.fetch(new Request(`https://relay.example/tg/${role}`, {
    method: 'POST',
    headers: {
      'x-telegram-bot-api-secret-token':
        secret ?? (role === 'monkey' ? MONKEY_HOOK : FRIEND_HOOK),
      'content-type': 'application/json',
    },
    body: JSON.stringify(body),
  }), env);
}

const replies = () => telegramCalls
  .filter((call) => call.url.endsWith('/sendMessage'))
  .map((call) => call.body);
const lastReply = () => replies().at(-1)?.text ?? '';

async function sha256hex(text) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

async function provisioned() {
  const env = makeEnv();
  assert.equal((await send(env, '/provision')).status, 200);
  telegramCalls = [];
  return env;
}

async function paired(env, role, chatId, code = '123456') {
  assert.equal((await send(env, '/pair', { body: { role, code } })).status, 200);
  await webhook(env, role, update(chatId, `/pair ${code}`));
  telegramCalls = [];
}

/** Stand melden, wie es der PC per /sync tut - receivedAt wird die aktuelle Fake-Zeit. */
async function reportState(env, state = {}) {
  const body = {
    state: {
      balanceSeconds: 600,
      earnedSeconds: 600,
      dailyGrantMinutes: 30,
      capMinutes: 240,
      counting: false,
      lastAccrualDate: '2026-08-16',
      tzOffsetMinutes: 0,
      ...state,
    },
  };
  assert.equal((await send(env, '/sync', { body })).status, 200);
}

const DAY = 86_400_000;

// ------------------------------------------------------------------ Tests

describe('routing and authentication', () => {
  test('GET / answers without secrets', async () => {
    const response = await worker.fetch(new Request('https://relay.example/'), makeEnv());
    assert.equal(response.status, 200);
    assert.match(await response.text(), /running/);
  });

  test('/info requires the sync secret and names version 3', async () => {
    const env = makeEnv();
    assert.equal((await send(env, '/info', { method: 'GET', secret: 'wrong' })).status, 401);

    const response = await send(env, '/info', { method: 'GET' });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { version: 3, secretBindings: true });
  });

  test('PC endpoints reject a wrong or missing bearer', async () => {
    assert.equal((await send(makeEnv(), '/sync', { secret: 'wrong' })).status, 401);

    const response = await worker.fetch(new Request('https://relay.example/sync', {
      method: 'POST', body: '{}',
    }), makeEnv());
    assert.equal(response.status, 401);
  });

  test('a missing or too short SYNC_SECRET locks everything', async () => {
    assert.equal((await send(makeEnv({ SYNC_SECRET: undefined }), '/sync')).status, 401);
    assert.equal(
      (await send(makeEnv({ SYNC_SECRET: 'short' }), '/sync', { secret: 'short' })).status,
      401);
  });

  test('unknown methods and paths are refused', async () => {
    assert.equal((await send(makeEnv(), '/sync', { method: 'PUT' })).status, 405);
    assert.equal((await send(makeEnv(), '/does-not-exist')).status, 404);
  });

  test('a broken JSON body is a bad request', async () => {
    assert.equal((await send(makeEnv(), '/sync', { raw: 'kein json {{{' })).status, 400);
  });

  test('internal failures never leak details', async () => {
    // Ohne KV-Binding knallt es intern - nach aussen bleibt es ein stummer 500er.
    const response = await send(makeEnv({ KV: undefined }), '/sync');
    assert.equal(response.status, 500);
    assert.deepEqual(await response.json(), { error: 'internal error' });
  });
});

describe('provision', () => {
  test('registers both webhooks and stores only hashes in KV', async () => {
    const env = makeEnv();
    const response = await send(env, '/provision');
    assert.equal(response.status, 200);

    assert.equal(telegramCalls.length, 2);
    const [monkeyCall, friendCall] = telegramCalls;
    assert.ok(monkeyCall.url.includes(`/bot${MONKEY_TOKEN}/setWebhook`));
    assert.equal(monkeyCall.body.url, 'https://relay.example/tg/monkey');
    assert.equal(monkeyCall.body.secret_token, MONKEY_HOOK);
    assert.deepEqual(monkeyCall.body.allowed_updates, ['message']);
    assert.equal(monkeyCall.body.drop_pending_updates, true);
    assert.ok(friendCall.url.includes(`/bot${FRIEND_TOKEN}/setWebhook`));
    assert.equal(friendCall.body.url, 'https://relay.example/tg/friend');

    const config = await env.KV.get('config', 'json');
    assert.equal(config.monkeyTokenHash, await sha256hex(MONKEY_TOKEN));
    assert.equal(config.friendTokenHash, await sha256hex(FRIEND_TOKEN));
    assert.deepEqual(config.monkeyChats, []);
    assert.deepEqual(config.friendChats, []);
    for (const forbidden of ['monkeyToken', 'friendToken', 'monkeyHookSecret', 'friendHookSecret'])
      assert.equal(config[forbidden], undefined, `${forbidden} must not be in KV`);
  });

  test('migrating a v1 config keeps pairings and drops every plaintext credential', async () => {
    const env = makeEnv();
    await env.KV.put('config', JSON.stringify({
      monkeyToken: MONKEY_TOKEN,
      friendToken: FRIEND_TOKEN,
      monkeyHookSecret: 'old-monkey-hook',
      friendHookSecret: 'old-friend-hook',
      monkeyChats: [111],
      friendChats: [222],
    }));

    assert.equal((await send(env, '/provision')).status, 200);

    const config = await env.KV.get('config', 'json');
    assert.deepEqual(config.monkeyChats, [111]);
    assert.deepEqual(config.friendChats, [222]);
    assert.equal(typeof config.monkeyTokenHash, 'string');
    for (const forbidden of ['monkeyToken', 'friendToken', 'monkeyHookSecret', 'friendHookSecret'])
      assert.equal(config[forbidden], undefined, `${forbidden} must not remain in KV`);
  });

  test('a replaced bot starts with zero pairings, the kept one survives', async () => {
    const env = await provisioned();
    const config = await env.KV.get('config', 'json');
    config.monkeyChats = [111];
    config.friendChats = [222];
    await env.KV.put('config', JSON.stringify(config));

    const newFriend = `777777:${'E'.repeat(35)}`;
    const rotated = { ...env, FRIEND_BOT_TOKEN: newFriend };
    assert.equal((await send(rotated, '/provision')).status, 200);

    const updated = await env.KV.get('config', 'json');
    assert.deepEqual(updated.monkeyChats, [111]);
    assert.deepEqual(updated.friendChats, []);
  });

  test('missing or equal secret bindings are a server-side error', async () => {
    const missing = await send(makeEnv({ MONKEY_BOT_TOKEN: undefined }), '/provision');
    assert.equal(missing.status, 500);
    assert.match((await missing.json()).error, /binding/);

    const equal = await send(makeEnv({ FRIEND_BOT_TOKEN: MONKEY_TOKEN }), '/provision');
    assert.equal(equal.status, 500);
  });

  test('a failing setWebhook aborts without touching the config', async () => {
    telegramResponder = () => ({ ok: false, description: 'bot token revoked' });
    const env = makeEnv();

    const response = await send(env, '/provision');

    assert.equal(response.status, 502);
    assert.match((await response.json()).error, /setWebhook/);
    assert.equal(await env.KV.get('config'), null);
  });
});

describe('pairing', () => {
  test('the PC stores only a hash of the code, with clamped lifetime', async () => {
    const env = makeEnv();
    assert.equal((await send(env, '/pair', {
      body: { role: 'friend', code: '123456', ttlSeconds: 999_999 },
    })).status, 200);

    const pair = await env.KV.get('pair', 'json');
    assert.equal(pair.role, 'friend');
    assert.equal(pair.codeHash, await sha256hex('friend:123456'));
    assert.equal(pair.attempts, 0);
    assert.equal(pair.expiresAt, now + 3_600_000);
    assert.equal(JSON.stringify(pair).includes('123456'), false);
  });

  test('bad role or code format is refused', async () => {
    assert.equal((await send(makeEnv(), '/pair', { body: { role: 'admin', code: '123456' } })).status, 400);
    assert.equal((await send(makeEnv(), '/pair', { body: { role: 'friend', code: '12345' } })).status, 400);
  });

  test('the right code pairs the chat exactly once', async () => {
    const env = await provisioned();
    await send(env, '/pair', { body: { role: 'friend', code: '123456' } });

    await webhook(env, 'friend', update(222, '/pair 123456'));

    assert.match(lastReply(), /Paired/);
    const config = await env.KV.get('config', 'json');
    assert.deepEqual(config.friendChats, [222]);
    assert.equal(await env.KV.get('pair'), null); // Der Code ist verbraucht.
  });

  test('five wrong tries burn the code', async () => {
    const env = await provisioned();
    await send(env, '/pair', { body: { role: 'friend', code: '123456' } });

    for (let i = 0; i < 5; i++) {
      await webhook(env, 'friend', update(222, '/pair 000000'));
      assert.match(lastReply(), /didn't work/);
    }

    // Selbst der richtige Code hilft jetzt nicht mehr.
    await webhook(env, 'friend', update(222, '/pair 123456'));
    assert.match(lastReply(), /No valid pairing code/);
    assert.deepEqual((await env.KV.get('config', 'json')).friendChats, []);
  });

  test('an expired code is worthless', async () => {
    const env = await provisioned();
    await send(env, '/pair', { body: { role: 'friend', code: '123456', ttlSeconds: 600 } });

    now += 601_000;
    await webhook(env, 'friend', update(222, '/pair 123456'));

    assert.match(lastReply(), /No valid pairing code/);
  });

  test('a code for one bot does not pair the other', async () => {
    const env = await provisioned();
    await send(env, '/pair', { body: { role: 'friend', code: '123456' } });

    await webhook(env, 'monkey', update(111, '/pair 123456'));

    assert.match(lastReply(), /No valid pairing code/);
    assert.deepEqual((await env.KV.get('config', 'json')).monkeyChats, []);
  });

  test('at most four chats per role - the oldest is evicted', async () => {
    const env = await provisioned();
    for (const chatId of [111, 222, 333, 444, 555])
      await paired(env, 'friend', chatId);

    assert.deepEqual((await env.KV.get('config', 'json')).friendChats, [222, 333, 444, 555]);
  });

  test('an unpaired chat only ever gets the pairing hint', async () => {
    const env = await provisioned();
    await reportState(env);

    await webhook(env, 'friend', update(999, '/status'));

    assert.match(lastReply(), /isn't paired/);
  });
});

describe('telegram webhooks', () => {
  test('without configuration nothing is revealed', async () => {
    const env = makeEnv();
    const response = await webhook(env, 'friend', update(222, '/status'));
    assert.equal(response.status, 200);
    assert.equal(replies().length, 0);
  });

  test('a wrong webhook secret is rejected', async () => {
    const env = await provisioned();
    const response = await webhook(env, 'friend', update(222, '/status'), 'wrong-secret');
    assert.equal(response.status, 401);
  });

  test('group chats and non-text updates are ignored', async () => {
    const env = await provisioned();
    await paired(env, 'friend', 222);

    await webhook(env, 'friend', update(222, '/status', 'group'));
    await webhook(env, 'friend', { message: { chat: { id: 222, type: 'private' } } });
    await webhook(env, 'friend', {});

    assert.equal(replies().length, 0);
  });
});

describe('status projection', () => {
  test('without any report there is no data', async () => {
    const env = await provisioned();
    await paired(env, 'monkey', 111);

    await webhook(env, 'monkey', update(111, '/status'));

    assert.match(lastReply(), /No data from the PC yet/);
  });

  test('online status shows balance and the running clock', async () => {
    const env = await provisioned();
    await paired(env, 'monkey', 111);
    await reportState(env, { counting: true });

    await webhook(env, 'monkey', update(111, '/status'));

    assert.match(lastReply(), /Balance: 10 min/);
    assert.match(lastReply(), /clock is running/);
    assert.doesNotMatch(lastReply(), /Commands:/); // Der Monkey-Bot kann nur /status.
  });

  test('days offline are projected exactly like the engine would credit them', async () => {
    const env = await provisioned();
    await paired(env, 'friend', 222);
    await reportState(env); // 10 min Guthaben, 30 min/Tag, Deckel 240 min

    now += 6 * DAY;
    await webhook(env, 'friend', update(222, '/status'));

    // 600 s + 6 x 1800 s = 11 400 s -> 3 h 10 min, Ersparnis identisch -> Stufe 5.
    assert.match(lastReply(), /Balance: 3 h 10 min/);
    assert.match(lastReply(), /monkey stage 5\/5/);
    assert.match(lastReply(), /PC: off/);
    assert.match(lastReply(), /Includes 6 daily top-up/);
    assert.match(lastReply(), /Tomorrow: 3 h 40 min/);
    assert.match(lastReply(), /Commands: \/status, \/add MIN/);
  });

  test('the projection respects the cap', async () => {
    const env = await provisioned();
    await paired(env, 'monkey', 111);
    await reportState(env, { balanceSeconds: 13_800, earnedSeconds: 0 });

    now += 10 * DAY;
    await webhook(env, 'monkey', update(111, '/status'));

    // 13 800 s + ein einziger Tag a 1800 s erreicht den Deckel von 14 400 s.
    assert.match(lastReply(), /Balance: 4 h 00 min/);
    assert.match(lastReply(), /Includes 1 daily top-up/);
    assert.doesNotMatch(lastReply(), /Tomorrow:/);
  });

  test('the timezone decides when a new day begins', async () => {
    const env = await provisioned();
    await paired(env, 'monkey', 111);

    // UTC ist schon der 16., aber lokal (UTC-12) erst der 15. - kein neuer Tag.
    now = Date.UTC(2026, 7, 16, 2, 0, 0);
    await reportState(env, { lastAccrualDate: '2026-08-15', tzOffsetMinutes: -720 });
    await webhook(env, 'monkey', update(111, '/status'));
    assert.match(lastReply(), /Balance: 10 min/);

    // Lokal (UTC+12) ist dagegen bereits der 16. - ein Tag wird gutgeschrieben.
    await reportState(env, { lastAccrualDate: '2026-08-15', tzOffsetMinutes: 720 });
    await webhook(env, 'monkey', update(111, '/status'));
    assert.match(lastReply(), /Balance: 40 min/);
  });

  test('the status text knows nothing of pausing', async () => {
    const env = await provisioned();
    await paired(env, 'monkey', 111);
    await reportState(env);

    await webhook(env, 'monkey', update(111, '/status'));

    assert.doesNotMatch(lastReply(), /pause/i);
  });
});

describe('friend commands', () => {
  async function friendReady(state = {}) {
    const env = await provisioned();
    await paired(env, 'friend', 222);
    await reportState(env, state);
    telegramCalls = [];
    return env;
  }

  const queuedCommands = async (env) => {
    const { keys } = await env.KV.list({ prefix: 'cmd:' });
    const commands = [];
    for (const key of keys) commands.push(await env.KV.get(key.name, 'json'));
    return commands;
  };

  test('before the first report nothing can be queued', async () => {
    const env = await provisioned();
    await paired(env, 'friend', 222);

    await webhook(env, 'friend', update(222, '/add 30'));

    assert.match(lastReply(), /hasn't reported in yet/);
    assert.equal((await queuedCommands(env)).length, 0);
  });

  test('/add validates its argument but knows no per-go limit', async () => {
    const env = await friendReady();

    await webhook(env, 'friend', update(222, '/add'));
    assert.match(lastReply(), /Usage: \/add/);

    await webhook(env, 'friend', update(222, '/add nope'));
    assert.match(lastReply(), /Usage: \/add/);

    await webhook(env, 'friend', update(222, '/add 5000001'));
    assert.match(lastReply(), /surely a typo/);

    assert.equal((await queuedCommands(env)).length, 0);

    // Weit ueber dem alten Deckel, aber gewollt: der Freund gibt, was er will.
    await webhook(env, 'friend', update(222, '/add 500'));
    assert.match(lastReply(), /Adding 500 min/);
    assert.equal((await queuedCommands(env))[0].minutes, 500);
  });

  test('/add queues the command and tells whether the PC is on', async () => {
    const env = await friendReady();

    await webhook(env, 'friend', update(222, '/add 30'));
    assert.match(lastReply(), /within about half a minute/);

    now += 100_000; // PC seit gut anderthalb Minuten still -> gilt als aus
    await webhook(env, 'friend', update(222, '/add 15'));
    assert.match(lastReply(), /as soon as it next starts/);

    // Die IDs sind zufaellige UUIDs - die KV-Reihenfolge ist es damit auch.
    const commands = (await queuedCommands(env))
      .map((c) => ({ type: c.type, minutes: c.minutes, chatId: c.chatId }))
      .sort((a, b) => a.minutes - b.minutes);
    assert.deepEqual(commands, [
      { type: 'add', minutes: 15, chatId: 222 },
      { type: 'add', minutes: 30, chatId: 222 },
    ]);
  });

  test('/banana queues the whole-day unlock without needing an argument', async () => {
    const env = await friendReady();

    await webhook(env, 'friend', update(222, '/banana'));
    assert.match(lastReply(), /Setting the rest of the day free/);

    const commands = await queuedCommands(env);
    assert.equal(commands.length, 1);
    assert.equal(commands[0].type, 'banana');
    assert.equal(commands[0].minutes, 0);
    assert.equal(commands[0].chatId, 222);
  });

  test('/pause and /resume are gone - the friend only gets help', async () => {
    const env = await friendReady();

    await webhook(env, 'friend', update(222, '/pause 60'));
    assert.match(lastReply(), /Commands: \/status/);
    await webhook(env, 'friend', update(222, '/resume'));
    assert.match(lastReply(), /Commands: \/status/);

    assert.equal((await queuedCommands(env)).length, 0);
  });

  test('a full queue refuses further commands', async () => {
    const env = await friendReady();
    for (let i = 0; i < 20; i++)
      await env.KV.put(`cmd:${i}`, JSON.stringify({ id: `${i}`, type: 'add', minutes: 1, chatId: 222 }));

    await webhook(env, 'friend', update(222, '/add 30'));

    assert.match(lastReply(), /queue is full/);
    assert.equal((await queuedCommands(env)).length, 20);
  });

  test("the monkey bot never queues commands - it only knows /status", async () => {
    const env = await provisioned();
    await paired(env, 'monkey', 111);
    await reportState(env);

    await webhook(env, 'monkey', update(111, '/add 30'));
    assert.match(lastReply(), /Command: \/status/);

    await webhook(env, 'monkey', update(111, '/banana'));
    assert.match(lastReply(), /Command: \/status/);

    assert.equal((await queuedCommands(env)).length, 0);
  });
});

describe('sync', () => {
  test('receipts delete the command and notify the sender', async () => {
    const env = await provisioned();
    await paired(env, 'friend', 222);
    await reportState(env);
    await webhook(env, 'friend', update(222, '/add 30'));
    const [command] = (await env.KV.list({ prefix: 'cmd:' })).keys;
    const id = command.name.slice('cmd:'.length);
    telegramCalls = [];

    const response = await send(env, '/sync', {
      body: { results: [{ id, ok: true, message: 'Added 30 min.' }] },
    });

    assert.equal(response.status, 200);
    assert.deepEqual((await response.json()).commands, []);
    assert.equal((await env.KV.list({ prefix: 'cmd:' })).keys.length, 0);
    assert.equal(replies().length, 1);
    assert.equal(replies()[0].chat_id, 222);
    assert.match(replies()[0].text, /✅ Added 30 min\./);
  });

  test('queued commands are handed to the PC', async () => {
    const env = await provisioned();
    await paired(env, 'friend', 222);
    await reportState(env);
    await webhook(env, 'friend', update(222, '/add 30'));
    await webhook(env, 'friend', update(222, '/add 60'));

    const response = await send(env, '/sync', { body: {} });

    const { commands } = await response.json();
    assert.equal(commands.length, 2);
    const add = commands.find((c) => c.minutes === 30);
    assert.equal(add.type, 'add');
    assert.equal(typeof add.id, 'string');
    assert.equal(commands.find((c) => c.minutes === 60).type, 'add');
  });

  test('unknown receipt ids are ignored quietly', async () => {
    const env = await provisioned();

    const response = await send(env, '/sync', {
      body: { results: [{ id: 'gibtsnicht', ok: true, message: 'x' }] },
    });

    assert.equal(response.status, 200);
    assert.equal(replies().length, 0);
  });
});

describe('reset', () => {
  test('deregisters both webhooks and wipes the KV store', async () => {
    const env = await provisioned();
    await reportState(env);
    await send(env, '/pair', { body: { role: 'friend', code: '123456' } });
    await env.KV.put('cmd:x', JSON.stringify({ id: 'x', type: 'add', minutes: 1 }));
    telegramCalls = [];

    const response = await send(env, '/reset');

    assert.equal(response.status, 200);
    const hookCalls = telegramCalls.filter((c) => c.url.endsWith('/deleteWebhook'));
    assert.equal(hookCalls.length, 2);
    assert.ok(hookCalls.some((c) => c.url.includes(`/bot${MONKEY_TOKEN}/`)));
    assert.ok(hookCalls.some((c) => c.url.includes(`/bot${FRIEND_TOKEN}/`)));

    for (const key of ['config', 'state', 'pair', 'cmd:x'])
      assert.equal(await env.KV.get(key), null);
  });
});
