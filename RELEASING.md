# Releasing a new version

1. Bump the version in **all three** places (there is no single source of truth):
   - `GlamSource.csproj` (`<Version>`, `<AssemblyVersion>`) — cosmetic only, `GenerateAssemblyInfo` is `false` so this does NOT affect the compiled DLL.
   - `Properties/AssemblyInfo.cs` (`AssemblyVersion`, `AssemblyFileVersion`) — this is the version that actually ends up in the built DLL.
   - `GlamSource.json` (`AssemblyVersion`) — the plugin manifest bundled into the build.
   - `repo.json` (`AssemblyVersion`) — the custom Dalamud plugin repo manifest; also bump `LastUpdated` (unix timestamp).
2. Commit and push to `main`.
3. GitHub Actions (`.github/workflows/build.yml`) builds on Windows and uploads `latest.zip`/`GlamSource.zip`/`repo.json` to the `LATEST` GitHub Release — check `gh run list` / `gh release view LATEST` to confirm it succeeded.
4. `repo.json`'s `DownloadLink*` fields point directly at that Release asset (`releases/download/LATEST/latest.zip`), so nothing else needs building or committing by hand.

**Gotcha already hit once:** the Dalamud client reads the repo manifest from the `repo.json` *release asset* (`releases/download/LATEST/repo.json`), not from the repo file directly — that asset is a separate upload from the git-tracked `repo.json`, so a version bump can go completely unnoticed by installed clients if the CI step ever stops uploading it. It was manually uploaded once and then silently went stale for over a week (client kept seeing an old version, no update offered) before this line got added to CI. If updates ever silently stop reaching people again, check `gh api repos/Primov1x/GlamSource/releases/tags/LATEST --jq '.assets[] | {name, updated_at}'` for a stale `repo.json` first.

**Do not** hand-build/commit a `dist/` folder — that used to exist, was never kept in sync with the actual release pipeline, and caused a whole "pushed 0.0.0.128 but the client still shows 0.0.0.127" investigation. It's gitignored now; the GitHub Release is the only real deliverable.
