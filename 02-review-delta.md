repo: ArbiScan
branch: main
latest_commit_sha: 9e2efd5353fff5c10bb665e3d1000e7306410967
generated_at_utc: 2026-04-02T09:54:57Z

# 02 Review Delta

Previous external review snapshot: not present in repository history.

This file establishes the initial external review baseline and records the review-relevant changes that led to the current state. The concrete delta below is based on the recent commits `1f0312dca175b4df475dc71e4bc9b24b7c7c13fd` and `9e2efd5353fff5c10bb665e3d1000e7306410967`.

## 1. Architecture / structure changes

- No project split/merge or solution restructuring was introduced.
- Review artifact structure was absent before this snapshot; `00-repo-tree.txt`, `01-critical-files-map.md`, and `02-review-delta.md` are now added as persistent external review navigation files.
- Runtime health flow gained a dedicated stateful component:
  `ArbiScan.Core/Services/QuoteStalenessTracker.cs`
  This is a structural extraction from inline health-threshold logic into an explicit service-level tracker.

## 2. Domain / strategy / business logic changes

- Behavioural change:
  stale quote detection no longer flips immediately when `DataAge > QuoteStalenessThresholdMs`.
- New logic now requires the stale condition to remain true continuously for a confirmation window before setting `BybitStale` / `BinanceStale`.
- Added new config parameter:
  `QuoteStalenessConfirmationMs`
  in:
  `ArbiScan.Core/Configuration/AppSettings.cs`
- `ArbiScan.Scanner/ScannerWorker.cs` now calls `QuoteStalenessTracker` during market snapshot health evaluation.
- Earlier config-only change from commit `1f0312d` raised:
  `QuoteStalenessThresholdMs`
  from previous value to `3000` in runtime and example config files.
- Net effect of recent changes:
  the threshold increase alone was insufficient for Bybit.
  The confirmation-window logic is the meaningful correctness change.

## 3. Execution lifecycle / workers / orchestration changes

- `ScannerWorker` health evaluation path changed:
  stale detection is now stateful across loop iterations instead of stateless per snapshot.
- No new workers, hosted services, schedulers, or restart/recovery loops were added.
- Main scan cadence, summary timers, and shutdown flush flow remain unchanged.

## 4. Persistence / schema changes

- No SQLite schema changes were introduced.
- No new tables, columns, migrations, or repository contracts were added.
- The new stale-confirmation behavior affects persisted health event frequency and semantics, but not storage shape.

## 5. External integrations / exchange / API changes

- No exchange client package swap or new provider was introduced.
- No REST/websocket subscription shape changed in:
  `ArbiScan.Exchanges.Binance/BinanceSpotExchangeAdapter.cs`
  `ArbiScan.Exchanges.Bybit/BybitSpotExchangeAdapter.cs`
- Behavioural integration impact:
  Bybit health degradation should no longer trigger on short normal update gaps alone; this changes externally observed Telegram and health-event output.

## 6. Tests added / updated

- Added:
  `ArbiScan.Tests/QuoteStalenessTrackerTests.cs`
- New regression coverage verifies:
  - stale state requires continuous confirmation time;
  - stale state resets after fresh quote updates;
  - explicit tracker reset clears exchange-local stale history.
- Existing broad math/summary tests in:
  `ArbiScan.Tests/CoreMathTests.cs`
  were not structurally expanded in this delta.

## 7. Known remaining gaps

- External review snapshot history did not exist before this baseline, so future deltas can be more precise than this initial bootstrap file.
- There is still no dedicated integration-test suite for live exchange adapter behavior or websocket gap simulation.
- Persistence schema remains inline inside repository code; there are no standalone migrations to review for schema evolution.
- The repository still operates as a single-process, single-symbol scanner. Multi-symbol orchestration, order execution, and richer recovery simulations are still outside current scope.

## Added / modified / deleted files in the current review-relevant delta

### Commit `1f0312dca175b4df475dc71e4bc9b24b7c7c13fd`

- Modified:
  `ArbiScan.Scanner/appsettings.json`
- Modified:
  `config/appsettings.example.json`
- Deleted:
  none
- Added:
  none
- Purpose:
  attempted to reduce false stale detections by increasing the quote staleness threshold to `3000 ms`.

### Commit `9e2efd5353fff5c10bb665e3d1000e7306410967`

- Added:
  `ArbiScan.Core/Services/QuoteStalenessTracker.cs`
- Added:
  `ArbiScan.Tests/QuoteStalenessTrackerTests.cs`
- Modified:
  `ArbiScan.Core/Configuration/AppSettings.cs`
- Modified:
  `ArbiScan.Scanner/ScannerWorker.cs`
- Modified:
  `ArbiScan.Scanner/appsettings.json`
- Modified:
  `ArbiScan.Scanner/appsettings.Development.json`
- Modified:
  `config/appsettings.example.json`
- Deleted:
  none
- Purpose:
  fix false Bybit stale alerts by introducing a confirmation window before the worker marks quotes stale.
