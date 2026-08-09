<img src="assets/monkey.png" width="120" align="right" alt="Monkey">

# Monkey

**A daily screen time allowance for your own PC. Whatever you don't use doesn't
vanish — it rolls over.**

Monkey is a small Windows tool for people who'd like to spend less time staring
at a screen, without relying on willpower alone. When your time runs out, Monkey
signs you out. Anything that adds time or pauses the limit needs a master
password.

---

## Why monkey?

Because monkey has had enough.

Enough of endless feeds engineered by very smart people whose actual job is to
keep monkey scrolling. Enough of opening the laptop "for five minutes" and
resurfacing two hours later with nothing to show for it. No more tralalero tralala, no more bombardiro crocodilo.
Monkey does not need to know what the tung tung tung sahur is. Monkey wants his
afternoon back.

So monkey put a lock on the door and gave the key to a friend.

---

## The idea

Most screen time tools hand you the same thing every day: two hours today, two
hours tomorrow. Skip a day and you get nothing for it. Burn through it by lunch
and your evening is gone.

Monkey treats it like a **Piggy Bank** instead:

- Every day a fresh allowance lands in the account (**30 minutes** by default).
- **Whatever you don't spend stays there** and piles up — up to a cap.
- Being frugal actually pays off: a few quiet days fund the long film evening on
  Saturday.

That's the part that matters. You're not just being capped, you're learning to
**budget** — and the limit stops feeling like a punishment, because you never
lose time, you only move it.

The second idea is **not doing this alone**. Give the master password to someone
you trust. Then "just five more minutes" isn't an argument with yourself at 1am.

---

## What it actually does

- **Shows your remaining time** in a small overlay in the top right. Hide it,
  recolour it, drop the background so only the number floats there. Click it to
  open the control panel. Hover it to see the other number (time used vs. time
  left).
- **Warns you in good time** — 10 minutes left by default — with a window that
  never steals focus, so it won't eat your keystrokes or tab you out of a game.
- **Signs you out** when the balance hits zero, with a grace period to save your
  work.
- **Counts fairly.** The clock stops while the session is locked, the
  screensaver is on or the display has switched itself off, and sleep costs
  you nothing.
- **Doesn't let itself be switched off in passing.** The service guards itself
  and puts its locks back after every start.

---

## Install

There is **one file**:
**[⬇ Download MonkeySetup.exe](https://github.com/fcspcs/Monkey/releases/latest/download/MonkeySetup.exe)**
— always the newest release. The service and the display are tucked inside it.

1. Download it, **double-click**, and say yes to the Windows prompt.
2. Pick **Install**, choose your daily allowance, set a master password. Done.

**To remove it:** run the same file and pick **Remove** — it asks for the master
password.

> Requires the **.NET 8 Desktop Runtime** (Windows 10/11, 64-bit). If it's
> missing, Windows offers the download on first launch.

Older versions are on the [releases page](https://github.com/fcspcs/Monkey/releases).
The build straight from the current source lives in
[`dist/MonkeySetup.exe`](dist/MonkeySetup.exe) — that one is rebuilt on every
change and hasn't necessarily been through a release.


---

## Settings

Fixed when you install (can't be changed afterwards):

| | |
|---|---|
| Top-up limit | 4 hours per go — repeatable, but never more in one shot |

Changeable any time in the control panel (double-click the tray icon; needs the
master password):

| | Default |
|---|---|
| Daily allowance | 30 minutes |
| Balance cap | 240 minutes |
| Warning at | 10 minutes left |
| Grace before sign-out | 90 seconds |
| Buffer after signing in with an empty balance | 120 seconds |
| Automatic updates (signed releases only) | on |

The **cap** matters more than it looks. Without it, one long holiday leaves you
with a balance so fat the whole thing stops meaning anything.

The grace periods can't be milked: with an empty balance you get the full
grace **three times in a row** — signing in again and again (or locking and
unlocking) doesn't stack up free minutes. From the fourth time on there are
only 10 seconds before sign-out, until the balance is topped up again (daily
allowance or master password), which resets the count.

---

## Controls

| | |
|---|---|
| Control panel | Double-click the tray icon, or click the overlay |
| Show/hide overlay | `Ctrl` + `Alt` + `Shift` + `T` |
| See the other number | Hover the overlay |
| Change how it looks | Right-click the tray icon |

---

## Remote control via Telegram (optional)

Monkey can talk to **two Telegram bots** — and it keeps working **even while
the PC is off**:

- **Monkey's bot** answers `/status`: balance, saved time, monkey stage.
- **The friend's bot** can additionally top up time (`/add 30`), pause the
  limit (`/pause 60`) and end the pause (`/resume`) — **from anywhere, without
  the master password ever leaving the PC**. Commands sent while the PC is off
  wait in a queue and apply on the next start.

This works through a tiny relay — a **Cloudflare Worker** — that you deploy
into your **own free Cloudflare account**. Nothing is hosted by this project
and no credentials live in this repository. The worker stores the last
reported state and, since the daily top-up is completely predictable, it can
answer *exactly* even days after the PC last reported in.

### Setup (about ten minutes, once)

1. **Two bots:** message [@BotFather](https://t.me/BotFather) in Telegram,
   send `/newbot` twice (one bot for Monkey, one for the friend) and keep both
   tokens. The friend can create their bot themselves and only hand the token
   over for step 4.
2. **Worker:** create a free account at
   [dash.cloudflare.com](https://dash.cloudflare.com) → *Workers & Pages* →
   *Create Worker* → paste the contents of
   [`cloud/worker.js`](cloud/worker.js) → *Deploy*. Note the
   `https://….workers.dev` URL.
3. **Two settings on the worker:** under *Settings* add a **KV namespace
   binding** named `KV`, and a **secret** named `SYNC_SECRET` — its value
   comes from the next step.
4. **Connect:** open Monkey's control panel → **Telegram** tab. Click
   *Generate* to create the sync secret (copy it into `SYNC_SECRET` on the
   worker first), enter the worker URL and both bot tokens, type the master
   password and hit *Save & connect*.
5. **Pairing:** still in the Telegram tab, create a one-time code per bot;
   each person sends `/pair CODE` to *their* bot. Done.

### What keeps this safe

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

Don't want it? Just leave the Telegram tab empty — everything works exactly
as before. Disconnecting later removes the webhooks and wipes the worker.

---

## Automatic updates

Monkey keeps itself current: the service checks the project's
[releases](https://github.com/fcspcs/Monkey/releases) a few times a day and
installs a newer version by itself — **no master password, no reinstalling**.
The balance, the master password and all settings survive an update untouched.

No password is needed because the mechanism can only move in one direction:

- Every release carries a manifest (`update.json`) **signed with the
  project's update key**; the matching public key is baked into the installed
  service. No valid signature — no update. Even someone who controls the
  network (or installs their own root certificate) can't feed Monkey a doctored
  "new version", and that matters, because a fake empty update would otherwise
  be the cheapest way around the limit.
- Only **strictly newer** versions are accepted, so an old (once genuinely
  signed) release can't be replayed to downgrade.
- The download lands in the locked data folder, is checked against the signed
  hash, and only then swaps the program files and restarts the service.

Turn it off any time in the control panel (needs the master password — turning
updates *off* is a decision the password holder makes).

**For maintainers and forks:** auto-update stays dormant until an update key
exists. Run `pwsh tools/new-update-key.ps1` once, commit the public key it
writes to `assets/update-key.pem`, and store the private key as the GitHub
Actions secret `UPDATE_SIGNING_KEY` — from then on every release is signed
automatically. Forks also change the repository name at the top of
`src/Monkey.Service/UpdateWorker.cs` so their installs pull from their fork.

---

**Being honest about how far this goes:** on Windows a local administrator isn't
a security boundary — that's Microsoft's own position, and no program without a
signed kernel driver changes it (commercial tools included). So Monkey leans on
lots of small locks that keep putting each other back up. Getting around it is
possible, but it takes deliberate effort rather than a moment of weakness. That's
exactly the bar we're aiming for.

---

## Build it yourself

```powershell
git clone https://github.com/fcspcs/Monkey.git
cd Monkey
.\build.ps1     # produces dist\MonkeySetup.exe
```

Needs the .NET 8 SDK. The installer in `dist/` is rebuilt automatically on every
change, and pushing a tag like `v1.1` publishes it as a release (GitHub Actions).

---

## Licence

The source code is MIT — see [LICENSE](LICENSE).

The images in `assets/` are memes and are **not** mine, so they're **not**
covered by the MIT licence. If you reuse this project, bring your own artwork —
`assets/make-icon.ps1` turns any PNG into the icon the build needs.
