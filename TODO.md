## TODO-Liste für GlamSource

### [ ] XIVAPI & Garland Tools Integration (In Progress)
- [ ] Forschung der Endpunkte https://v2.xivapi.com/api/docs#tag/assets
  - [ ] Item-Daten (/item/{id})
  - [ ] Mount-Daten (/mount/{id})
  - [ ] Asset-Daten (/asset/{id})
- [ ] Implementierung der API-Zugriffslogik
  - [ ] Caching-System (XIVAPICache.cs)
  - [ ] Asynchrone Anfragen mit Framework.RunOnTick
- [ ] UI-Komponentenentwicklung
  - [ ] Tab "Gegenstandsquellen" in MainWindow
  - [ ] Tab "Mount-Acquisition Guide" in ConfigWindow
  - [ ] Suchfunktion & Visualisierungen (Icons/Tooltip)
- [ ] Datenverarbeitung
  - [ ] Integration Garland Tools-Daten (lokal gecachte JSON)
  - [ ] Fallback-System bei API-Ausfällen
- [ ] Sicherheit & Qualität
  - [ ] Fehlerbehandlung für Netzwerkaufrufe
  - [ ] Automatische Aktualisierung des lokalen Caches
  - [ ] Einhaltung aller Dalamud-Regeln ([PluginService], keine direkten Dateizugriffe)