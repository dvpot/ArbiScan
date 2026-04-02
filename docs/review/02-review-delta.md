repo: ArbiScan
branch: main
latest_commit_sha: cf035ecb1fccf9021af41ce25144289f0f254e71
generated_at_utc: 2026-04-02T09:54:57Z

# 02 Review Delta

Previous external review snapshot: not present in repository history.

This file establishes the initial external review baseline and records only the most review-relevant recent runtime changes.

## 1. Architecture / structure changes

- Root project directories were renamed from `ArbiScan.*` to shorter operational names:
  `Core`, `Infrastructure`, `Exchanges.Binance`, `Exchanges.Bybit`, `Scanner`, `Tests`.
- Review and handoff docs were grouped under `docs/`:
  `docs/QUICK-HANDOFF.md`
  `docs/review/00-repo-tree.txt`
  `docs/review/01-critical-files-map.md`
  `docs/review/02-review-delta.md`
- Runtime health flow was split into two explicit components instead of living only inside `ScannerWorker`:
  `Core/Services/QuoteStalenessTracker.cs`
  `Core/Services/HealthReportGenerator.cs`

## 2. Domain / strategy / business logic changes

- Behavioural change:
  stale quote detection no longer flips immediately when `DataAge > QuoteStalenessThresholdMs`.
- New logic now requires the stale condition to remain true continuously for a confirmation window before setting `BybitStale` / `BinanceStale`.
- Added config parameter:
  `QuoteStalenessConfirmationMs`
- Added richer health-notification config:
  `HealthStateChangeMinNotifyIntervalSeconds`
  `RequireStableHealthyBeforeNotifyMs`
  `RequireStableDegradedBeforeNotifyMs`
- `ScannerWorker` now logs stale diagnostics with status, update timestamps, callback timestamp, depth counts, best bid/ask, and threshold-crossing warnings at `>1000ms`, `>2000ms`, and `>threshold`.
- Net effect:
  the threshold increase alone was insufficient for Bybit; confirmation-window plus diagnostics is now the review-relevant correctness change.

## 3. Execution lifecycle / workers / orchestration changes

- `ScannerWorker` health evaluation path changed:
  stale detection is stateful across loop iterations instead of stateless per snapshot.
- Telegram health-state notifications now have debounce/cooldown behavior independent of health-event persistence.
- Worker now exports a dedicated health report on the same hourly/daily/cumulative schedule as trading summaries.
- No new workers, hosted services, schedulers, or restart/recovery loops were added.
- Main scan cadence, summary timers, and shutdown flush flow remain unchanged.
- Delivery flow changed from publish-only CI to publish-plus-deploy CI:
  the GitHub workflow now has a VPS deploy stage over SSH after GHCR publish.

## 4. Persistence / schema changes

- No SQLite schema changes were introduced.
- No new tables, columns, migrations, or repository contracts were added.
- Stale-confirmation behavior and Telegram debounce affect health-event frequency/interpretation, but not storage shape.
- New `health-<period>-<timestamp>.json` files are exported to reports storage; they are standalone report artifacts, not DB-backed entities.

## 5. External integrations / exchange / API changes

- No exchange client package swap or new provider was introduced.
- No REST/websocket subscription shape changed in:
  `Exchanges.Binance/BinanceSpotExchangeAdapter.cs`
  `Exchanges.Bybit/BybitSpotExchangeAdapter.cs`
- Exchange adapters now capture timestamp of the last successful order-book update callback for diagnostics.
- Behavioural integration impact:
  Bybit health degradation should no longer trigger on short normal update gaps alone, and stale alerts now carry enough runtime context for further diagnosis.

## 6. Tests added / updated

- Added:
  `Tests/QuoteStalenessTrackerTests.cs`
- Added:
  `Tests/HealthReportGeneratorTests.cs`
- New regression coverage verifies:
  - stale state requires continuous confirmation time;
  - stale state resets after fresh quote updates;
  - explicit tracker reset clears exchange-local stale history.
- New health-report coverage verifies:
  - per-exchange reconnect/resync/stale counting;
  - longest stale interval calculation;
  - last stale/reconnect/resync timestamps.

## 7. Known remaining gaps

- External review snapshot history did not exist before this baseline, so future deltas can be more precise than this initial bootstrap file.
- There is still no dedicated integration-test suite for live exchange adapter behavior or websocket gap simulation.
- Persistence schema remains inline inside repository code; there are no standalone migrations to review for schema evolution.
- The repository still operates as a single-process, single-symbol scanner. Multi-symbol orchestration, order execution, and richer recovery simulations are still outside current scope.
- The new diagnostics improve evidence quality, but practical usefulness of collected `window-events` still depends on real runtime outputs and has not been proven by code review alone.
- Automatic VPS deploy still depends on GitHub repository secrets being configured correctly.

## Most review-relevant changed files in the current delta

- `Scanner/ScannerWorker.cs`
  runtime diagnostics, telegram debounce, enriched heartbeat, dedicated health report export.
- `Core/Services/QuoteStalenessTracker.cs`
  stateful stale confirmation logic.
- `Core/Services/HealthReportGenerator.cs`
  dedicated aggregated health report generation.
- `Exchanges.Binance/BinanceSpotExchangeAdapter.cs`
  adapter-level last update callback timestamp capture.
- `Exchanges.Bybit/BybitSpotExchangeAdapter.cs`
  adapter-level last update callback timestamp capture.
- `Core/Configuration/AppSettings.cs`
  new telegram anti-drift/debounce config.
- `config/telegramsettings.example.json`
  production-facing example for the new Telegram health notification controls.
- `docs/QUICK-HANDOFF.md`
  operational continuity file for future chats and deployment/runtime context.
- `scripts/collect-analysis-bundle.sh`
  helper for packaging the runtime artifacts needed for deeper `BybitStale` analysis.
- `Tests/QuoteStalenessTrackerTests.cs`
  stale-confirmation regression coverage.
- `Tests/HealthReportGeneratorTests.cs`
  health report aggregation coverage.
