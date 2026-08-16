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
- **Shows you the pattern.** The control panel's **Statistics** page charts screen
  time per day, how the banked balance rose and fell, or which weekdays actually
  cost you — one chart at a time, over the last 7, 30 or 90 days, with the plain
  numbers one click away. The service keeps a year of daily totals; nothing leaves
  the PC.
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

The grace periods can't be milked: with an empty balance you get three in full
length, then only 10 seconds until sign-out — until the next top-up resets the
count.

---

## Controls

| | |
|---|---|
| Control panel | Double-click the tray icon, or click the overlay |
| Show/hide overlay | `Ctrl` + `Alt` + `Shift` + `T` |
| See the other number | Hover the overlay |
| Change how it looks | Right-click the tray icon |

---

## Telegram (optional)

Check the balance and top up from your phone — **even while the PC is off**:

- **Monkey's bot** answers `/status`: balance, saved time, monkey stage.
- **The friend's bot** can also add time, pause and resume — remote control
  without the master password ever leaving the PC.

It runs through a tiny relay in your **own free Cloudflare account** — this
project hosts nothing. Open the control panel's **Telegram** tab: its assistant
walks through four separate screens. It prepares copyable BotFather commands,
bot names and unique username suggestions; shows where Cloudflare's current
dashboard hides the Account ID; opens a pre-filled, account-scoped API-token
form with only **Workers Scripts: Edit** and **Workers KV Storage: Edit**; then
deploys the Worker and KV store, installs the secrets and connects both bots.
Cloudflare and bot credentials are used once and are not stored on the PC. Bot
tokens and webhook keys are encrypted Cloudflare **secret bindings**, never
ordinary KV values. The wizard links directly to Cloudflare's official
[Account ID](https://developers.cloudflare.com/fundamentals/account/find-account-and-zone-ids/)
and [API token](https://developers.cloudflare.com/fundamentals/api/get-started/create-token/)
instructions if the dashboard labels change.

The same tab checks the deployed Worker version and safely updates its code.
Updates preserve pairings, the last status and queued commands. Monkey asks for
a fresh, revocable Cloudflare token for each deployment or update and never
keeps that token. A complete removal button deletes the exact managed Worker,
its secret bindings and its dedicated KV store.

The old manual/Wrangler route remains available as an
**[advanced fallback](cloud/README.md)**.

Don't want it? Leave the Telegram tab in the control panel empty and nothing
changes.

---

## Automatic updates

Monkey updates itself from the project's releases — no master password, no
reinstalling; balance, password and settings survive untouched. That's safe
without a password because it only moves one way: nothing installs unless it
is **newer** and **signed with the project's update key**, so a doctored
"update" can't be used to sneak past the limit — not even by someone who
controls the network.

Turn it off any time in the control panel. Maintainers and forks must configure
the signing key once before publishing — `powershell -ExecutionPolicy Bypass -File tools/new-update-key.ps1` walks
through it, and the release workflow refuses to publish an unsigned update.

---

**Being honest about how far this goes:** on Windows a local administrator isn't
a security boundary. An administrator can ask Task Scheduler to run arbitrary
code as `LocalSystem`, which is also the identity used by Monkey's service. No ACL
inside Monkey can distinguish those two uses of the same identity. The locks here
therefore prevent casual or accidental shutdown; they cannot safely promise more.

For an actual boundary, use a **standard Windows account day to day** and keep the
credentials of a separate administrator account with the person who holds the
Monkey master password. Keep BitLocker and Secure Boot enabled as well, otherwise
offline boot media can bypass the Windows account boundary. Developers who need
administrator access should treat Monkey as tamper-resistant, not tamper-proof.

---

## Build it yourself

```powershell
git clone https://github.com/fcspcs/Monkey.git
cd Monkey
.\test.ps1      # runs the test suite (engine, protocol, Telegram relay)
.\build.ps1     # produces dist\MonkeySetup.exe
```

Needs the .NET 8 SDK (plus Node.js for the Telegram relay tests). The installer
in `dist/` is rebuilt automatically on every change, and pushing a tag like
`v1.1` publishes it as a release (GitHub Actions) - both only after the same
tests have passed.

---

## Licence

The source code is MIT — see [LICENSE](LICENSE).

The images in `assets/` are memes and are **not** mine, so they're **not**
covered by the MIT licence. If you reuse this project, bring your own artwork —
`assets/make-icon.ps1` turns any PNG into the icon the build needs.
