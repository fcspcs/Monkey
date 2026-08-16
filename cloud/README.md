# Telegram remote control — advanced manual setup

The recommended setup now lives in Monkey's **Telegram** tab and deploys this
Worker automatically. Use this document only when you deliberately want to
manage the Worker yourself or the guided deployment cannot be used.

Monkey can talk to **two Telegram bots**, and it keeps answering **even while
the PC is off**:

| Bot | Commands |
|---|---|
| **Monkey's bot** | `/status` — balance, saved time, monkey stage |
| **The friend's bot** | `/status`, `/add 30`, `/pause 60`, `/resume` |

The friend controls Monkey **from anywhere, without the master password ever
leaving the PC**. Commands sent while the PC is off wait in a queue and apply
on the next start.

It works through a tiny relay — a **Cloudflare Worker** — that you deploy into
your **own free Cloudflare account**. Nothing is hosted by this project and no
credentials live in this repository. The worker stores the last reported state
and, since the daily top-up is completely predictable, it answers *exactly*
even days after the PC last reported in.

## Manual setup

1. **Two bots:** message [@BotFather](https://t.me/BotFather) in Telegram,
   send `/newbot` twice (one bot for Monkey, one for the friend) and keep both
   tokens. The friend can create their bot themselves and only hand the token
   over for step 4.
2. **Worker:** create a free account at
   [dash.cloudflare.com](https://dash.cloudflare.com) → *Workers & Pages* →
   *Create Worker* → paste the contents of [`worker.js`](worker.js) →
   *Deploy*. Note the `https://….workers.dev` URL.
3. **Bindings:** under *Settings* add a **KV namespace binding** named `KV`.
   Add these five values as **encrypted secrets**, never as plain variables:
   `SYNC_SECRET`, `MONKEY_BOT_TOKEN`, `FRIEND_BOT_TOKEN`,
   `MONKEY_WEBHOOK_SECRET`, `FRIEND_WEBHOOK_SECRET`. The control panel's
   *Generate* button creates `SYNC_SECRET`; use independent random hexadecimal
   values of at least 32 characters for both webhook secrets.
4. **Connect:** open Monkey's control panel → **Telegram** tab, enter the Worker
   URL and the same `SYNC_SECRET`, type the master password and select
   *Connect existing Worker*. The Worker registers the webhooks from its secret
   bindings. Bot and webhook tokens never enter KV.
5. **Pairing:** still in the Telegram tab, create a one-time code per bot;
   each person sends `/pair CODE` to *their* bot. Done.

Prefer the command line? [`wrangler.toml`](wrangler.toml) lists the required
Wrangler commands in its header.

## What keeps this safe

- The **master password never leaves the PC** — it isn't sent to, or stored
  on, Telegram or the worker. Being paired *is* the friend's authority, so
  nobody is tempted to save the password anywhere.
- The worker can only do what the service explicitly allows: add time (within
  the per-go top-up limit), pause, resume. It cannot change settings or
  passwords, and it cannot unlock or remove Monkey.
- The bot and webhook tokens are encrypted **Cloudflare secret bindings** and
  never KV values. They are not kept on the PC; the sync secret is stored
  DPAPI-encrypted so that an administrator merely reading the state file sees
  ciphertext.
- Every path is authenticated: the PC proves itself to the worker with the
  sync secret, Telegram proves itself with per-bot webhook secrets, and chats
  prove themselves once with a pairing code (5 tries, 10 minutes, then it
  burns).

## Turning it off

The Telegram tab offers two choices. **Disconnect only** removes both webhooks
and wipes KV state and commands but leaves the Worker and its secret bindings
in Cloudflare. **Remove Worker & data** additionally uses a fresh one-time API
token to delete that managed Worker, all its secret bindings and its dedicated
KV store. With an empty Telegram tab, Monkey behaves exactly as if none of this
existed.
