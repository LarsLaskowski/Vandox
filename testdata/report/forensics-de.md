# Vandox-Forensikbericht

- Erstellt: 2026-10-06 07:00 UTC
- Logzeilen: 123456 (erste 2026-09-29 00:00 UTC, letzte 2026-10-06 06:00 UTC)
- Beobachtete Stunden: 168
- Prozess-Snapshots: 2016 (erste 2026-09-29 00:05 UTC, letzte 2026-10-06 05:55 UTC)
- Rückblick in Minuten: 60
- Alle Zeiten sind UTC.

## Übersicht

- Ausfälle: 13, OOM-Kills: 29
- Erster Ausfall: 2026-09-30 01:05 UTC
- Letzter Ausfall: 2026-10-05 22:40 UTC
- Tageszeit 02:00–03:00 UTC: 3 von 13 Ausfällen (23,1 %) gegenüber 4,2 % der beobachteten Stunden
- Backup läuft: 3 von 3 Ausfällen (100,0 %) gegenüber 4,2 % der beobachteten Stunden
- Regeln ohne Ergebnis: Neustart (zu viele Neustarts), Panel-Speicher hoch (kein Panel-Speicher)

## Wiederkehrende Muster

### Tageszeit (UTC)

| Stunde | Ausfälle | Anteil der Ausfälle | Anteil der beobachteten Stunden | Ergebnis |
| --- | ---: | ---: | ---: | --- |
| 02:00–03:00 | 3 | 23,1 % | 4,2 % | erkannt |
| 05:00–06:00 | 1 | 7,7 % | 4,2 % | nicht erkannt |

### Aktivitäten vor den Ausfällen

| Aktivität | Ausfälle damit | Ausfälle bekannt | Anteil der Ausfälle | Anteil der beobachteten Stunden | Ergebnis | Grund |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| Backup läuft | 3 | 3 | 100,0 % | 4,2 % | erkannt | — |
| Neustart | 0 | 0 | — | — | unbekannt | zu viele Neustarts |
| Panel-Speicher hoch | 0 | 0 | — | — | unbekannt | kein Panel-Speicher |

Schwelle für hohen Panel-Speicher: 68 MiB

## Speicherverbraucher

### Dauerhafte Verbraucher

- ` mariadbd `: in 100,0 % der Snapshots, Mittel 300 MiB, Spitze 400 MiB

### Vorübergehende Verbraucher

- ` pzstd `: in 0,6 % der Snapshots, Spitze 150 MiB am 2026-10-02 06:59 UTC

## Ausfälle

### Ausfall #7 am 2026-10-02 06:59 UTC

- Zeitfenster: 2026-10-02 05:59 UTC – 2026-10-02 07:29 UTC
- Kills: 5
- Opfer: ` mariadbd ` (2), ` php-fpm ` (1), unbekannt (1)
- Nicht gespeicherte Kills: 1
- Wachstumskandidaten: ` pzstd ` +768 MiB, ` php-fpm ` +2 MiB
- Größte Verbraucher: ` apache2 ` 400 MiB, ` mariadbd ` 300 MiB
- Aktivitäten vor dem Ausfall:
  - Backup läuft: ja
  - Neustart: ja (letzter Neustart 2026-10-02 06:30 UTC)
  - Panel-Speicher hoch: nein
- Dienstereignisse im Rückblick: 2

### Ausfall #8 am 2026-10-03 06:59 UTC

- Zeitfenster: 2026-10-03 05:59 UTC – 2026-10-03 07:29 UTC
- Kills: 1
- Opfer: ` nginx ` (1)
- Wachstumskandidaten: unbekannt
- Größte Verbraucher: unbekannt
- Aktivitäten vor dem Ausfall:
  - Backup läuft: nein
  - Neustart: nein
  - Panel-Speicher hoch: unbekannt
- Dienstereignisse im Rückblick: 0

### Ausfall #9 am 2026-10-04 06:59 UTC

- Zeitfenster: 2026-10-04 05:59 UTC – 2026-10-04 07:29 UTC
- Kills: 1
- Opfer: ` cron ` (1)
- Wachstumskandidaten: keine
- Größte Verbraucher: keine
- Aktivitäten vor dem Ausfall:
  - Backup läuft: unbekannt
  - Neustart: unbekannt
  - Panel-Speicher hoch: ja
- Dienstereignisse im Rückblick: 0

## Hinweise

- Ein Muster wird gemeldet, wenn mindestens 2 von mindestens 3 bekannten Ausfällen eine Eigenschaft teilen, ihr Anteil über dem Anteil der beobachteten Stunden liegt und die Wahrscheinlichkeit für so viele Ausfälle durch Zufall höchstens 1 % beträgt (1 % / 24 für eine Stunde des Tages). Beobachtete Stunden sind UTC-Stunden mit einer Zeile des Systemlogs (journal oder syslog); eine Aktivität wird über den Rückblick vor dem ersten Kill beurteilt. Eine Regel, die nicht entschieden werden kann, steht als unbekannt mit ihrem Grund da, nie als nicht erkannt.
- Der Bericht behält höchstens 1000000 Prozess-Snapshot-Punkte; die späteren Snapshots wurden nicht verwendet, daher sind Backup und Panel-Speicher unbekannt.
- Der Bericht behält höchstens 10000 Neustarts; die späteren Neustarts wurden nicht verwendet, daher ist die Neustart-Regel unbekannt.
- Der Bericht behält höchstens 100000 Abschnitte aufeinanderfolgender beobachteter Stunden; die späteren Stunden wurden nicht verwendet, daher sind Backup, Neustart und Panel-Speicher unbekannt.
- 7 Programmeinträge wurden nicht verfolgt, weil der Bericht höchstens 4096 Programme verfolgt.
- Die Zahl der Vorfälle hat sich zwischen den beiden Durchläufen über die gespeicherten Vorfälle geändert, daher passen Regeln und Ausfallliste möglicherweise nicht zusammen; vandoxd incidents und den Bericht erneut ausführen.
