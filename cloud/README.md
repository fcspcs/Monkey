# Telegram remote control — setup guide

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

## Setup (about ten minutes, once)

1. **Two bots:** message [@BotFather](https://t.me/BotFather) in Telegram,
   send `/newbot` twice (one bot for Monkey, one for the friend) and keep both
   tokens. The friend can create their bot themselves and only hand the token
   over for step 4.
2. **Worker:** create a free account at
   [dash.cloudflare.com](https://dash.cloudflare.com) → *Workers & Pages* →
   *Create Worker* → paste the contents of [`worker.js`](worker.js) →
   *Deploy*. Note the `https://….workers.dev` URL.
3. **Two settings on the worker:** under *Settings* add a **KV namespace
   binding** named `KV`, and a **secret** named `SYNC_SECRET` — its value
   comes from the next step.
4. **Connect:** open Monkey's control panel → **Telegram** tab. Click
   *Generate* to create the sync secret (copy it into `SYNC_SECRET` on the
   worker first), enter the worker URL and both bot tokens, type the master
   password and hit *Save & connect*.
5. **Pairing:** still in the Telegram tab, create a one-time code per bot;
   each person sends `/pair CODE` to *their* bot. Done.

Prefer the command line? [`wrangler.toml`](wrangler.toml) has the three
Wrangler commands in its header.

## What keeps this safe

- The **master password never leaves the PC** — it isn't sent to, or stored
  on, Telegram or the worker. Being paired *is* the friend's authority, so
  nobody is tempted to save the password anywhere.
- The worker can only do what the service explicitly allows: add time (within
  the per-go top-up limit), pause, resume. It cannot change settings or
  passwords, and it cannot unlock or remove Monkey.
- The bot tokens are handed to **your** worker once during setup and are not
  kept on the PC; the sync secret is stored DPAPI-encrypted so that even an
  administrator reading the state file only sees ciphertext.
- Every path is authenticated: the PC proves itself to the worker with the
  sync secret, Telegram proves itself with per-bot webhook secrets, and chats
  prove themselves once with a pairing code (5 tries, 10 minutes, then it
  burns).

## Turning it off

Disconnect in the Telegram tab — that removes both webhooks and wipes the
worker's stored tokens, state and command queue. Or never set it up: with an
empty Telegram tab, Monkey behaves exactly as if none of this existed.
