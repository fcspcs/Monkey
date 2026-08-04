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
- **Counts fairly.** The clock stops while the session is locked or the
  screensaver is on, and sleep costs you nothing.
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

The **cap** matters more than it looks. Without it, one long holiday leaves you
with a balance so fat the whole thing stops meaning anything.

---

## Controls

| | |
|---|---|
| Control panel | Double-click the tray icon, or click the overlay |
| Show/hide overlay | `Ctrl` + `Alt` + `Shift` + `T` |
| See the other number | Hover the overlay |
| Change how it looks | Right-click the tray icon |

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
