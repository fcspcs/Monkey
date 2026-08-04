# TimeGuard

**Ein tägliches Zeitkontingent für den eigenen PC — nicht verbrauchte Zeit verfällt nicht, sondern wird übertragen.**

Windows-Tool für Leute, die weniger Zeit am Rechner verbringen wollen, ohne sich
dabei auf reine Willenskraft zu verlassen. Ist das Kontingent aufgebraucht,
meldet TimeGuard den Benutzer ab. Alles, was Zeit hinzufügt oder die Kontrolle
aussetzt, ist mit einem Master-Passwort geschützt.

---

## Die Idee dahinter

Die meisten Bildschirmzeit-Tools kennen nur „heute 2 Stunden, morgen wieder
2 Stunden". Wer heute nichts verbraucht, hat nichts davon — und wer sein Budget
mittags aufbraucht, sitzt abends auf dem Trockenen.

TimeGuard macht daraus ein **Konto**:

- Jeden Tag kommt ein Kontingent dazu (Voreinstellung **30 Minuten**).
- **Was du nicht verbrauchst, bleibt liegen** und wächst an — bis zu einer
  Obergrenze.
- Dadurch lohnt es sich, sparsam zu sein: Ein paar ruhige Tage finanzieren den
  langen Filmabend am Wochenende.

Das trainiert genau die Fähigkeit, um die es eigentlich geht — **sich Zeit
einzuteilen**, statt sie nur gedeckelt zu bekommen. Und es nimmt dem Limit die
Härte: Man verliert nichts, man verschiebt es.

Der zweite Gedanke ist die **Bindung an eine andere Person**: Das Master-Passwort
kannst du jemandem geben, dem du vertraust. Dann ist mehr Zeit nicht mehr eine
Frage der Selbstüberwindung im schwachen Moment, sondern eine, die du kurz
aussprechen musst. Genau diese kleine Hürde ist der Punkt.

---

## Was es praktisch tut

- **Zeigt die Restzeit** als kleines Overlay oben rechts. Ausblendbar, Farbe
  wählbar, Hintergrund abschaltbar. Umschaltbar zwischen „verbleibend" und
  „schon genutzt"; der Mauszeiger darüber zeigt jeweils den anderen Wert.
- **Warnt rechtzeitig** (Voreinstellung: bei 10 Minuten Restzeit) mit einem
  Fenster, das nie den Fokus stiehlt — es unterbricht also weder Tippen noch
  Spielen.
- **Meldet ab**, wenn das Guthaben leer ist — mit Vorwarnung zum Speichern.
- **Zählt fair**: Die Uhr steht bei gesperrter Sitzung und laufendem
  Bildschirmschoner; Ruhezustand kostet nichts.
- **Lässt sich nicht nebenbei abschalten**: Der Dienst schützt sich selbst und
  stellt seine Sperren nach jedem Start wieder her.

---

## Installieren

Es gibt **eine einzige Datei**: [`dist/TimeGuardSetup.exe`](dist/TimeGuardSetup.exe).
Dienst und Anzeige stecken darin.

1. Datei herunterladen und **doppelklicken**, die Windows-Abfrage bestätigen.
2. **`1` (Installieren)** wählen, Tagesbudget festlegen, Master-Passwort setzen.

**Entfernen:** dieselbe Datei starten, **`2`** wählen — das verlangt das
Master-Passwort.

> Voraussetzung: **.NET 8 Desktop Runtime** (Windows 10/11, 64 Bit). Fehlt sie,
> bietet Windows den Download beim ersten Start an.

> **Das Master-Passwort ist der einzige Schlüssel.** Bewahre es außerhalb des
> Rechners auf — oder gib es der Person deines Vertrauens.

---

## Einstellungen

Beim Installieren festgelegt (später nicht mehr änderbar):

| | |
|---|---|
| Nachlegen je Vorgang | höchstens 4 Stunden — beliebig oft wiederholbar |

Jederzeit änderbar über die Master-Steuerung (Doppelklick auf das Tray-Symbol,
Master-Passwort nötig):

| | Voreinstellung |
|---|---|
| Tagesbudget | 30 Minuten |
| Höchstguthaben (Deckel) | 240 Minuten |
| Warnung bei | 10 Minuten Restzeit |
| Vorwarnung vor Abmeldung | 90 Sekunden |
| Puffer nach Anmeldung mit leerem Konto | 120 Sekunden |

Der **Deckel** ist wichtig: Ohne ihn sammelt ein langer Urlaub ein Kontingent an,
das das ganze System entwertet.

---

## Bedienung

| | |
|---|---|
| Master-Steuerung | Doppelklick auf das Tray-Symbol |
| Overlay ein/aus | `Strg` + `Alt` + `Umschalt` + `T` |
| Anderen Wert sehen | Mauszeiger über das Overlay |
| Darstellung ändern | Rechtsklick auf das Tray-Symbol |

---

## Wie es aufgebaut ist

```
TimeGuardService.exe   Windows-Dienst (LocalSystem). Zählt, entscheidet,
                       prüft das Passwort, meldet ab, schützt sich selbst.
        ▲  Named Pipe
        ▼
TimeGuardAgent.exe     Pro Benutzer: Overlay, Tray, Warnung, Master-Fenster.
                       Reine Anzeige, ohne eigene Befugnis.
```

Das Master-Passwort wird nur als PBKDF2-SHA256-Hash gespeichert und
**ausschließlich im Dienst** geprüft. Das Guthaben gilt systemweit, nicht pro
Benutzerkonto.

**Ehrlich zur Reichweite:** Ein lokaler Administrator ist unter Windows laut
Microsofts eigenen Kriterien keine Sicherheitsgrenze — ein Programm ohne
signierten Kernel-Treiber kann das nicht ändern (auch kommerzielle Tools nicht).
TimeGuard setzt darum auf viele sich gegenseitig wiederherstellende Sperren: Ein
Umgehen ist aufwendig und bewusst, statt beiläufig. Genau darum geht es.

---

## Selbst bauen

```powershell
git clone https://github.com/fcspcs/TimeGuard.git
cd TimeGuard
.\build.ps1     # Ergebnis: dist\TimeGuardSetup.exe
```

Braucht das .NET 8 SDK. Die Installer-Datei im Repository wird bei jeder
Änderung automatisch neu gebaut (GitHub Actions).

---

## Lizenz

MIT — siehe [LICENSE](LICENSE).
