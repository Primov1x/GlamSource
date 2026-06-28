# Behebung der Dalamud-Inkompatibilität (API-Level 14 vs 15)

## Problembeschreibung
Das Plugin zeigt in Dalamud die Fehlermeldung "outdated and incompatible" an.
Die Analyse der kompilierten `bin/x64/Release/GlamSource.json` zeigt, dass das generierte Manifest `"DalamudApiLevel": 15` enthält. Das liegt daran, dass im Projekt-File (`GlamSource.csproj`) das Sdk `Dalamud.NET.Sdk/15.0.0` verwendet wird:
`<Project Sdk="Dalamud.NET.Sdk/15.0.0">`

Gleichzeitig geben jedoch unsere zentralen Index-Dateien `repo.json` und `manifest.json` für Dalamud-Benutzer `"DalamudApiLevel": 14` bzw. `"dalamud_api_level": 14` an. Da das kompilierte Plugin als API Level 15 markiert ist und die aktuelle Dalamud-Instanz (Dawntrail API 14) nach API Level 14 verlangt, blockiert Dalamud das Laden wegen dieser Diskrepanz (und stuft das Plugin als inkompatibel für API Level 14 ein oder umgekehrt).

## Lösung
Wir müssen sicherstellen, dass das Plugin explizit für API Level 14 kompiliert wird, da dies die Version ist, für die das Plugin derzeit ausgerichtet ist (oder wie es in den Manifesten definiert ist).

### 1. CSPROJ anpassen
In der Datei `GlamSource/GlamSource.csproj` legen wir explizit fest, dass die Kompilation für `DalamudApiLevel: 14` erfolgen soll, indem wir die Property im `<PropertyGroup>`-Block hinzufügen:
```xml
<DalamudApiLevel>14</DalamudApiLevel>
```
Dies zwingt das `Dalamud.NET.Sdk`, im automatisch generierten `GlamSource.json` das API Level 14 und nicht 15 einzutragen.

### 2. Versions- und API-Konsistenz
Damit das Repo und das Plugin perfekt übereinstimmen und von Dalamud als kompatibles Update erkannt werden, synchronisieren wir die Versionen auf `0.0.0.3` (oder halten sie konsistent).
In `manifest.json` steht derzeit Version `0.0.0.1` und API `14`.
In `repo.json` steht Version `0.0.0.3` und API `14`.
In `GlamSource.csproj` steht Version `0.0.0.2` und (implizit) API `15` (vom SDK defaultet).

Wir aktualisieren alle Versionen auf eine einheitliche, fortlaufende Versionsnummer, d.h. **0.0.0.3**:
- `GlamSource.csproj`: `<Version>0.0.0.3</Version>` und `<DalamudApiLevel>14</DalamudApiLevel>`
- `manifest.json`: `"assembly_version": "0.0.0.3"`, `"testing_assembly_version": "0.0.0.3"` und `"dalamud_api_level": 14`
- `repo.json`: `"AssemblyVersion": "0.0.0.3"` und `"DalamudApiLevel": 14`

Wir prüfen auch die Eingabemanifestdatei `GlamSource/GlamSource.json`:
- `"ApplicableVersion": "any"` bleibt bestehen.

### 3. Verifikation
Wir pushen die Änderungen und überprüfen die GitHub Action. Durch das Hinzufügen von `<DalamudApiLevel>14</DalamudApiLevel>` wird das generierte Manifest in der ZIP mit API Level 14 gebündelt, was perfekt mit dem Repository-Eintrag in `repo.json` (API 14) harmoniert. Damit verschwindet die Fehlermeldung "outdated and incompatible" in Dalamud nachhaltig.
