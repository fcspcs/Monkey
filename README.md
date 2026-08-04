# TimeGuard

Tageskontingent für Computerzeit — mit dem Feature, das Time Boss Pro nicht hat:
**nicht verbrauchte Zeit verfällt nicht, sondern wird übertragen.**

Ist das Kontingent aufgebraucht, wird der Benutzer abgemeldet. Die Restzeit steht
als Overlay oben rechts. Pausieren, Zeit nachlegen, Einstellungen und das Entfernen
brauchen das Master-Passwort.

---

## Installieren

Es gibt **eine einzige Datei**: `dist\TimeGuardSetup.exe`. Dienst und Agent stecken
darin und werden bei der Installation herausgeschrieben.

1. Einmal bauen (ohne Adminrechte):

   ```powershell
   .\build.ps1
   ```

2. **`dist\TimeGuardSetup.exe` doppelklicken.** Windows fragt nach Adminrechten —
   bestätigen. Dann **`1` (Installieren)** wählen, Tagesbudget bestätigen und das
   Master-Passwort festlegen. Fertig.

**Entfernen:** dieselbe `TimeGuardSetup.exe` starten und **`2` (Entfernen)**
wählen. Das verlangt das Master-Passwort.

Zum Weitergeben oder Aufheben reicht diese eine Datei.

> **Das Master-Passwort ist der einzige Schlüssel.** Bewahre es außerhalb des
> Rechners auf (Passwortmanager auf dem Handy, Zettel). Ohne es lässt sich das
> Tool nicht regulär entfernen — das ist so gewollt.

> Voraussetzung: das **.NET 8 Desktop Runtime** (auf diesem Rechner vorhanden).
> Falls es auf einem anderen PC fehlt, bietet Windows den Download beim ersten
> Start automatisch an.

---

## Voreinstellungen

| | |
|---|---|
| Tagesbudget | **30 Minuten** |
| Höchstguthaben (Deckel) | **240 Minuten** — 8 Tage Sparen |
| Warnfenster bei | **10 Minuten** Restzeit |
| Vorwarnung vor Abmeldung | 90 Sekunden |
| Puffer nach Anmeldung mit leerem Konto | **120 Sekunden** |
| Nachlegen per Master-Passwort | höchstens **4 Stunden pro Vorgang** (beliebig oft) |

Alles im Master-Fenster änderbar.

---

## Bedienung

| | |
|---|---|
| Restzeit sehen | Overlay oben rechts, oder Zeiger über das Tray-Symbol |
| Anderen Wert sehen | Mauszeiger über das Overlay — dreht auf den jeweils anderen Wert |
| Hoch- statt runterzählen | Rechtsklick auf das Tray-Symbol → *Angemeldete Zeit hochzählen* |
| Overlay ein/aus | `Strg` + `Alt` + `Umschalt` + `T`, oder Rechtsklick auf das Tray-Symbol |
| Hintergrund an/aus | Rechtsklick auf das Tray-Symbol → *Hintergrund ausblenden* (dann bleibt nur die Zahl) |
| Farbe der Zahl | Rechtsklick auf das Tray-Symbol → *Farbe der Zahl* |
| Master-Steuerung | Doppelklick auf das Tray-Symbol |
| Pausieren | Master-Steuerung → Passwort → Dauer → *Pause starten* |
| Zeit nachlegen | Master-Steuerung → Passwort → Minuten (negativ zieht ab) |

Das Overlay zeigt **keine Sekunden** — es aktualisiert sich alle zwei Sekunden.
Nur während der Vorwarnung vor der Abmeldung zählt es sekundengenau herunter.

Das Tray-Symbol färbt sich: grün → gelb (unter 15 min) → rot (unter 5 min),
blau bei Pause, grau wenn der Dienst nicht erreichbar ist.

Das Ausblenden des Overlays und das Beenden des Agent ändern nichts an der
Durchsetzung — der Agent zeigt nur an, entschieden wird im Dienst.

---

## Wie das Kontingent rechnet

Bei jedem Tageswechsel:

```
Guthaben = min(Guthaben + Tagesbudget, Höchstguthaben)
```

Bei 30 min/Tag und 240 min Deckel:

| Situation | Guthaben danach |
|---|---|
| 10 min übrig, 1 Tag vergangen | 40 min |
| 10 min übrig, 3 Tage vergangen | 100 min |
| 10 min übrig, 30 Tage vergangen | 240 min — der Deckel greift |

Verbraucht wird jede angemeldete Minute. Die Uhr steht nur, wenn die Sitzung
gesperrt ist oder der Bildschirmschoner läuft (beides abschaltbar). Schlaf- und
Ruhezustand kosten nichts.

### Wenn die Zeit ausgeht

1. Bei **10 Minuten** Restzeit erscheint ein Warnfenster. Es nimmt nie den Fokus.
2. Bei **0** beginnt die Vorwarnung: 90 Sekunden zum Speichern, danach Abmeldung.
3. Wer sich **mit bereits leerem Konto anmeldet**, bekommt **120 Sekunden** — das
   Notfallfenster, um per Master-Passwort Zeit nachzulegen.

Per Master-Passwort lassen sich **pro Vorgang höchstens 4 Stunden** nachlegen —
beliebig oft. Im Master-Fenster gibt es dafür `+30`- und `−30`-Knöpfe sowie ein
Feld für andere Werte. Abziehen ist unbegrenzt.

---

## Architektur

```
TimeGuardService.exe   Windows-Dienst als LocalSystem, Autostart.
                       Zählt, entscheidet, prüft das Passwort, meldet ab,
                       schützt sich selbst.

        ▲  Named Pipe (\\.\pipe\TimeGuard.v1)
        ▼

TimeGuardAgent.exe     Pro angemeldetem Benutzer. Overlay, Tray, Warnfenster,
                       Master-Fenster. Hat keinerlei eigene Befugnis.
```

Das Master-Passwort liegt nur als PBKDF2-SHA256-Hash (600 000 Runden) vor und
wird **ausschließlich im Dienst** geprüft. Nach fünf Fehlversuchen ist die Prüfung
60 Sekunden gesperrt. Das Guthaben ist **systemweit**, nicht pro Konto.

---

## Selbstschutz

Der Dienst richtet bei jedem Start und über eine Watchdog-Aufgabe fortlaufend
mehrere voneinander unabhängige Riegel wieder auf: Zugriffssperren auf seine
Daten, seinen Programmordner, seine Registrierung und sich selbst, Start auch im
abgesicherten Modus, und eine geplante Aufgabe, die ihn bei Bedarf neu aufsetzt.
Wird ein Riegel gelöst, stellt der Dienst ihn selbsttätig wieder her.

Regulär entfernen oder ändern lässt sich all das nur mit dem Master-Passwort.

### Grenzen — ehrlich

Absolute Unumgehbarkeit gibt es auf Windows für ein Programm ohne signierten
Kernel-Treiber nicht: Ein lokaler Administrator ist laut Microsofts eigenen
Sicherheitskriterien **keine Sicherheitsgrenze**. TimeGuard hebt die Hürde auf
dasselbe Niveau, auf dem auch kommerzielle Zeitkontrollen arbeiten (auch Time Boss
kommt ohne Kernel-Treiber aus) — viele sich gegenseitig wiederherstellende Riegel,
sodass ein Umgehen aufwendig und bewusst ist statt beiläufig.

Wirklich außerhalb der Reichweite dieser Software liegt der Zugriff **außerhalb des
laufenden Windows** (Start von einem anderen Medium, Ausbau des Datenträgers).
Dagegen hilft nur Geräteverschlüsselung mit einem BIOS-/UEFI-Passwort — eine
Einstellung des Rechners, nicht des Programms.

---

## Wenn etwas klemmt

**Overlay fehlt** — der Agent läuft nicht. Er startet nach der nächsten Anmeldung
automatisch wieder, oder von Hand:

```powershell
& "C:\Program Files\TimeGuard\TimeGuardAgent.exe"
```

**Guthaben oder Rest prüfen** — im Master-Fenster (Doppelklick auf das Tray-Symbol).

**Zeit ändern, pausieren, entfernen** — alles über das Master-Fenster bzw.
`install\Uninstall.ps1`, jeweils mit dem Master-Passwort.

---

## Entwicklung

Nur der **Debug-Build** kennt Testschalter (`--data-dir`, `--pipe`, `--dry-run`);
der ausgelieferte Release-Build enthält sie nicht.

```powershell
# Dienst gegen einen Testordner, ohne echte Abmeldung (nur Debug-Build)
$d = "$env:TEMP\tg-test"
$svc = ".\src\TimeGuard.Service\bin\Debug\net8.0-windows\win-x64\TimeGuardService.exe"
& $svc --data-dir $d --pipe timeguard.test --dry-run
```
