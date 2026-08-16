/**
 * Monkey Telegram relay — a Cloudflare Worker you deploy into YOUR OWN free
 * Cloudflare account. It answers your two Telegram bots even while the PC is
 * off, and it queues the friend's commands until the PC picks them up.
 *
 * The repository ships no credentials. Everything secret is yours:
 *   - SYNC_SECRET   Worker secret (Settings → Variables and Secrets). Generated
 *                   in Monkey's control panel, Telegram tab.
 *   - KV            A KV namespace binding named "KV" (Settings → Bindings).
 *   - Bot tokens    Stored as encrypted Cloudflare secret bindings. They never
 *                   live in KV and the PC does not retain them after setup.
 *   - Webhook keys  Stored as encrypted Cloudflare secret bindings as well.
 *
 * Security model, in short: the PC authenticates to this worker with the sync
 * secret (Bearer). Telegram authenticates its webhooks with per-bot secret
 * tokens this worker registered. Chats must pair with a one-time code before
 * any command or status is served. The master password never leaves the PC,
 * and the worker can only do what the PC-side engine allows: add time within
 * the per-go limit, pause, resume. It cannot change settings, passwords, or
 * unlock anything.
 */

const TOKEN_RE = /^\d{5,12}:[A-Za-z0-9_-]{30,64}$/;
const HOOK_SECRET_RE = /^[A-Za-z0-9_-]{16,128}$/;
const WORKER_VERSION = 2;
const MAX_QUEUE = 20;
const MAX_CHATS_PER_ROLE = 4;
const ONLINE_WINDOW_MS = 90_000; // letzter Sync juenger als das => PC gilt als an

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (err) {
      // Nie Interna oder gar Tokens nach aussen kippen.
      return json({ error: 'internal error' }, 500);
    }
  },
};

async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, '') || '/';

  if (request.method === 'GET' && path === '/')
    return new Response('Monkey Telegram relay is running.', { status: 200 });

  // Telegram-Webhooks authentifizieren sich per secret_token, nicht per Bearer.
  if (request.method === 'POST' && path === '/tg/monkey') return telegramUpdate(request, env, 'monkey');
  if (request.method === 'POST' && path === '/tg/friend') return telegramUpdate(request, env, 'friend');

  // Alles weitere ist der PC — nur mit dem Sync-Secret.
  if (!(await authorized(request, env))) return json({ error: 'unauthorized' }, 401);

  if (request.method === 'GET' && path === '/info')
    return json({ version: WORKER_VERSION, secretBindings: true });

  if (request.method !== 'POST') return json({ error: 'method not allowed' }, 405);
  if (path === '/provision') return provision(request, env, url.origin);
  if (path === '/pair') return pairSetup(request, env);
  if (path === '/sync') return sync(request, env);
  if (path === '/reset') return reset(env);

  return json({ error: 'not found' }, 404);
}

// ------------------------------------------------------------ PC endpoints

async function authorized(request, env) {
  if (typeof env.SYNC_SECRET !== 'string' || env.SYNC_SECRET.length < 16) return false;
  const header = request.headers.get('authorization') || '';
  if (!header.startsWith('Bearer ')) return false;
  return safeEqual(header.slice(7).trim(), env.SYNC_SECRET);
}

/** Secret-Bindings pruefen, beide Webhooks registrieren und altes KV migrieren. */
async function provision(request, env, origin) {
  const body = await readJson(request);
  if (!body) return json({ error: 'bad request' }, 400);

  const monkeyToken = env.MONKEY_BOT_TOKEN;
  const friendToken = env.FRIEND_BOT_TOKEN;
  const monkeyWebhookSecret = env.MONKEY_WEBHOOK_SECRET;
  const friendWebhookSecret = env.FRIEND_WEBHOOK_SECRET;
  if (!TOKEN_RE.test(monkeyToken || '') || !TOKEN_RE.test(friendToken || ''))
    return json({ error: 'a bot-token secret binding is missing or invalid' }, 500);
  if (!HOOK_SECRET_RE.test(monkeyWebhookSecret || '') || !HOOK_SECRET_RE.test(friendWebhookSecret || ''))
    return json({ error: 'a webhook secret binding is missing or invalid' }, 500);
  if (monkeyToken === friendToken)
    return json({ error: 'the two bot-token bindings must be different' }, 500);

  // Keine Geheimnisse in KV: Nur Hash-Fingerabdruecke entscheiden, ob Pairings
  // bei einer Aktualisierung zum selben Bot gehoeren. Die old.*Token-Pruefung
  // migriert Worker v1, ohne die bestehenden Pairings zu verlieren.
  const old = (await env.KV.get('config', 'json')) || {};
  const monkeyTokenHash = await sha256hex(monkeyToken);
  const friendTokenHash = await sha256hex(friendToken);
  const config = {
    monkeyTokenHash,
    friendTokenHash,
    monkeyChats: old.monkeyToken === monkeyToken || old.monkeyTokenHash === monkeyTokenHash
      ? old.monkeyChats || [] : [],
    friendChats: old.friendToken === friendToken || old.friendTokenHash === friendTokenHash
      ? old.friendChats || [] : [],
  };

  for (const role of ['monkey', 'friend']) {
    const result = await tg(role === 'monkey' ? monkeyToken : friendToken, 'setWebhook', {
      url: `${origin}/tg/${role}`,
      secret_token: role === 'monkey' ? monkeyWebhookSecret : friendWebhookSecret,
      allowed_updates: ['message'],
      drop_pending_updates: true,
    });
    if (!result.ok)
      return json({ error: `setWebhook for the ${role} bot failed: ${result.description || 'unknown'}` }, 502);
  }

  await env.KV.put('config', JSON.stringify(config));
  return json({ ok: true });
}

/** Einmalcode vom PC hinterlegen. Gespeichert wird nur ein Hash. */
async function pairSetup(request, env) {
  const body = await readJson(request);
  if (!body || (body.role !== 'monkey' && body.role !== 'friend') || !/^\d{6}$/.test(body.code || ''))
    return json({ error: 'bad request' }, 400);

  const ttl = Math.min(3600, Math.max(60, Number(body.ttlSeconds) || 600));
  await env.KV.put(
    'pair',
    JSON.stringify({
      role: body.role,
      codeHash: await sha256hex(`${body.role}:${body.code}`),
      expiresAt: Date.now() + ttl * 1000,
      attempts: 0,
    }),
    { expirationTtl: ttl + 60 },
  );
  return json({ ok: true });
}

/** Stand entgegennehmen, Quittungen zustellen, wartende Befehle zurueckgeben. */
async function sync(request, env) {
  const body = await readJson(request);
  if (!body || typeof body !== 'object') return json({ error: 'bad request' }, 400);

  if (body.state && typeof body.state === 'object')
    await env.KV.put('state', JSON.stringify({ ...body.state, receivedAt: Date.now() }));

  const config = await env.KV.get('config', 'json');

  const results = Array.isArray(body.results) ? body.results.slice(0, MAX_QUEUE) : [];
  for (const result of results) {
    if (!result || typeof result.id !== 'string') continue;
    const key = `cmd:${result.id}`;
    const command = await env.KV.get(key, 'json');
    if (!command) continue;

    await env.KV.delete(key);
    if (config?.friendTokenHash && command.chatId)
      await tg(env.FRIEND_BOT_TOKEN, 'sendMessage', {
        chat_id: command.chatId,
        text: `${result.ok ? '✅' : '⚠️'} ${String(result.message || '').slice(0, 300)}`,
      });
  }

  const commands = [];
  const list = await env.KV.list({ prefix: 'cmd:' });
  for (const key of list.keys.slice(0, MAX_QUEUE)) {
    const command = await env.KV.get(key.name, 'json');
    if (command) commands.push({ id: command.id, type: command.type, minutes: command.minutes || 0 });
  }

  return json({ commands });
}

/** Webhooks abmelden und KV leeren; Secret-Bindings loescht nur die Cloudflare-API. */
async function reset(env) {
  for (const token of [env.MONKEY_BOT_TOKEN, env.FRIEND_BOT_TOKEN])
    if (token) await tg(token, 'deleteWebhook', { drop_pending_updates: true });

  await env.KV.delete('config');
  await env.KV.delete('state');
  await env.KV.delete('pair');
  const list = await env.KV.list({ prefix: 'cmd:' });
  for (const key of list.keys) await env.KV.delete(key.name);

  return json({ ok: true });
}

// ------------------------------------------------------- Telegram webhooks

async function telegramUpdate(request, env, role) {
  const config = await env.KV.get('config', 'json');
  if (!config) return ok(); // noch nicht eingerichtet — nichts verraten

  const expected = role === 'monkey' ? env.MONKEY_WEBHOOK_SECRET : env.FRIEND_WEBHOOK_SECRET;
  const got = request.headers.get('x-telegram-bot-api-secret-token') || '';
  if (!expected || !(await safeEqual(got, expected))) return json({ error: 'unauthorized' }, 401);

  const update = await readJson(request);
  const message = update?.message;
  const text = typeof message?.text === 'string' ? message.text.trim() : '';
  const chatId = message?.chat?.id;

  // Nur direkte Chats. Gruppen haben hier nichts verloren.
  if (!text || typeof chatId !== 'number' || message.chat.type !== 'private') return ok();

  const token = role === 'monkey' ? env.MONKEY_BOT_TOKEN : env.FRIEND_BOT_TOKEN;
  const reply = (t) => tg(token, 'sendMessage', { chat_id: chatId, text: t });

  const [first, arg] = text.split(/\s+/, 2);
  const command = first.toLowerCase().split('@')[0];

  if (command === '/pair') {
    await handlePair(env, config, role, chatId, arg, reply);
    return ok();
  }

  const chats = role === 'monkey' ? config.monkeyChats : config.friendChats;
  if (!chats.includes(chatId)) {
    await reply("This chat isn't paired with Monkey yet. Get a pairing code from Monkey's control panel and send: /pair CODE");
    return ok();
  }

  const state = await env.KV.get('state', 'json');

  if (command === '/status') {
    await reply(statusText(state, role));
    return ok();
  }

  if (role === 'friend' && (command === '/add' || command === '/pause' || command === '/resume')) {
    await handleFriendCommand(env, state, command, arg, chatId, reply);
    return ok();
  }

  await reply(helpText(role));
  return ok();
}

async function handlePair(env, config, role, chatId, arg, reply) {
  const pair = await env.KV.get('pair', 'json');

  if (!pair || pair.role !== role || Date.now() > pair.expiresAt || pair.attempts >= 5) {
    await reply("No valid pairing code is active for this bot. Get a fresh one from Monkey's control panel.");
    return;
  }

  if (!arg || (await sha256hex(`${role}:${arg}`)) !== pair.codeHash) {
    pair.attempts += 1;
    const ttl = Math.max(60, Math.ceil((pair.expiresAt - Date.now()) / 1000) + 60);
    await env.KV.put('pair', JSON.stringify(pair), { expirationTtl: ttl });
    await reply("That code didn't work.");
    return;
  }

  const chats = role === 'monkey' ? config.monkeyChats : config.friendChats;
  if (!chats.includes(chatId)) {
    chats.push(chatId);
    while (chats.length > MAX_CHATS_PER_ROLE) chats.shift();
  }
  await env.KV.put('config', JSON.stringify(config));
  await env.KV.delete('pair');

  await reply(
    role === 'friend'
      ? 'Paired ✅  You can now use /status, /add 30, /pause 60 and /resume.'
      : 'Paired ✅  Send /status any time to check the balance.',
  );
}

async function handleFriendCommand(env, state, command, arg, chatId, reply) {
  if (!state) {
    await reply("The PC hasn't reported in yet — try again after Monkey has been running once.");
    return;
  }

  let queued;
  if (command === '/add') {
    const minutes = parseInt(arg, 10);
    const max = state.maxManualGrantMinutes || 240;
    if (!Number.isInteger(minutes) || minutes < 1) {
      await reply('Usage: /add MINUTES — for example /add 30');
      return;
    }
    if (minutes > max) {
      await reply(`At most ${max} min can be added per go. Send /add again for more.`);
      return;
    }
    queued = { type: 'add', minutes, action: `Adding ${minutes} min` };
  } else if (command === '/pause') {
    const minutes = parseInt(arg, 10);
    const max = state.maxPauseMinutes || 480;
    if (!Number.isInteger(minutes) || minutes < 1) {
      await reply('Usage: /pause MINUTES — for example /pause 60');
      return;
    }
    queued = { type: 'pause', minutes: Math.min(minutes, max), action: `Pausing for ${Math.min(minutes, max)} min` };
  } else {
    queued = { type: 'resume', minutes: 0, action: 'Ending the pause' };
  }

  const list = await env.KV.list({ prefix: 'cmd:' });
  if (list.keys.length >= MAX_QUEUE) {
    await reply("The command queue is full — the PC hasn't picked anything up in a long time.");
    return;
  }

  const id = crypto.randomUUID();
  await env.KV.put(
    `cmd:${id}`,
    JSON.stringify({ id, type: queued.type, minutes: queued.minutes, chatId, createdAt: Date.now() }),
    { expirationTtl: 14 * 24 * 3600 },
  );

  const online = Date.now() - (state.receivedAt || 0) < ONLINE_WINDOW_MS;
  await reply(
    online
      ? `${queued.action} — the PC is online, this should be done within about half a minute.`
      : `${queued.action} — the PC is off right now; it applies as soon as it next starts.`,
  );
}

// ------------------------------------------------------------- Status math

/**
 * Der Stand bei ausgeschaltetem PC ist vollstaendig vorhersagbar: pro lokalem
 * Kalendertag kommt die Tagesgutschrift dazu, bis zum Deckel. Das hier ist die
 * JS-Fassung von Accrue/Grant aus der GuardEngine.
 */
function project(state, nowMs) {
  const grant = (state.dailyGrantMinutes || 0) * 60;
  const cap = (state.capMinutes || 0) * 60;
  let balance = Math.max(0, state.balanceSeconds || 0);
  let earned = Math.max(0, state.earnedSeconds || 0);

  const days = daysSince(state.lastAccrualDate, state.tzOffsetMinutes || 0, nowMs);
  let credited = 0;
  for (let i = 0; i < Math.min(days, 400); i++) {
    if (balance >= cap) break;
    const before = balance;
    balance = Math.min(balance + grant, cap);
    earned += balance - before;
    credited++;
  }

  earned = Math.max(0, Math.min(earned, balance));
  const stage = grant > 0 ? Math.min(5, Math.max(1, Math.floor(earned / grant))) : 1;
  return { balance, earned, stage, credited, days };
}

function daysSince(lastAccrualDate, tzOffsetMinutes, nowMs) {
  if (typeof lastAccrualDate !== 'string') return 0;
  const parts = lastAccrualDate.split('-').map(Number);
  if (parts.length !== 3 || parts.some(Number.isNaN)) return 0;

  const last = Date.UTC(parts[0], parts[1] - 1, parts[2]);
  const local = new Date(nowMs + tzOffsetMinutes * 60_000);
  const today = Date.UTC(local.getUTCFullYear(), local.getUTCMonth(), local.getUTCDate());
  return Math.max(0, Math.round((today - last) / 86_400_000));
}

function statusText(state, role) {
  if (!state) return 'No data from the PC yet.';

  const now = Date.now();
  const online = now - (state.receivedAt || 0) < ONLINE_WINDOW_MS;
  const p = project(state, now);

  const lines = [
    `Balance: ${fmt(p.balance)}`,
    `Saved from daily allowances: ${fmt(p.earned)} → monkey stage ${p.stage}/5`,
  ];

  const pauseLeft = (state.pauseRemainingSeconds || 0) - (now - (state.receivedAt || now)) / 1000;
  if (pauseLeft > 0) lines.push(`The limit is paused for another ${fmt(pauseLeft)}.`);
  else if (online && state.counting) lines.push('The clock is running right now.');

  if (!online) {
    lines.push(`PC: off (last report ${fmt((now - (state.receivedAt || now)) / 1000)} ago).`);
    if (p.credited > 0) lines.push(`Includes ${p.credited} daily top-up(s) earned since then.`);
  }

  const cap = (state.capMinutes || 0) * 60;
  const grant = (state.dailyGrantMinutes || 0) * 60;
  if (grant > 0 && p.balance < cap)
    lines.push(`Tomorrow: ${fmt(Math.min(p.balance + grant, cap))}.`);

  if (role === 'friend') lines.push('Commands: /add MIN, /pause MIN, /resume');
  return lines.join('\n');
}

function helpText(role) {
  return role === 'friend'
    ? 'Commands: /status — balance and monkey stage, /add MIN — top up, /pause MIN — pause the limit, /resume — end the pause.'
    : 'Command: /status — balance, saved time and monkey stage.';
}

function fmt(seconds) {
  const s = Math.max(0, Math.round(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return h >= 1 ? `${h} h ${String(m).padStart(2, '0')} min` : `${m} min`;
}

// --------------------------------------------------------------- Plumbing

async function tg(token, method, params) {
  const response = await fetch(`https://api.telegram.org/bot${token}/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(params),
  });
  return await response.json().catch(() => ({ ok: false, description: 'bad response from Telegram' }));
}

async function readJson(request) {
  try {
    return await request.json();
  } catch {
    return null;
  }
}

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: { 'content-type': 'application/json' } });
}

function ok() {
  // Telegram will schlicht ein 200, sonst stellt es den Update erneut zu.
  return new Response('ok', { status: 200 });
}

async function sha256hex(text) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

/** Vergleich ohne Zeitleck: erst hashen, dann vergleichen. */
async function safeEqual(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string') return false;
  return (await sha256hex(`x:${a}`)) === (await sha256hex(`x:${b}`));
}
