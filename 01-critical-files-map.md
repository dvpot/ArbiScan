repo: ArbiScan
branch: main
latest_commit_sha: 9e2efd5353fff5c10bb665e3d1000e7306410967
generated_at_utc: 2026-04-02T09:54:57Z

# 01 Critical Files Map

## A. Entry Points / Bootstrap / Host Wiring

| Class / Component | File path | Purpose |
| --- | --- | --- |
| `Program` | `ArbiScan.Scanner/Program.cs` | Main process entrypoint. Builds host, loads config from env plus mounted config files, validates settings, wires DI, logging, adapters, repository, exporter, notifier, and registers `ScannerWorker`. |
| `AppSettings`, `TelegramSettings`, `ExchangeConnectionSettings` | `ArbiScan.Core/Configuration/AppSettings.cs` | Runtime configuration contract for scan loop, thresholds, buffers, storage, exchange credentials, and Telegram behavior. |
| `StoragePathBuilder`, `AppStoragePaths` | `ArbiScan.Infrastructure/Setup/StoragePathBuilder.cs`, `ArbiScan.Infrastructure/Setup/AppStoragePaths.cs` | Resolves mounted storage root into config/log/data/report paths used by the process. |
| `AppVersion` | `ArbiScan.Scanner/AppVersion.cs` | Exposes runtime version baked into the container and used in notifications. |
| GitHub publish workflow | `.github/workflows/build-test-publish.yml` | CI/CD bootstrap for restore/build/test and GHCR image publishing on pushes to `main`/`master`. |
| Docker runtime wiring | `Dockerfile`, `docker-compose.yml` | Container packaging and runtime mount/env wiring for VPS deployment. |

## B. Background Processing / Workers / Schedulers

| Class / Component | File path | Purpose |
| --- | --- | --- |
| `ScannerWorker` | `ArbiScan.Scanner/ScannerWorker.cs` | Single hosted background service that initializes adapters, runs the scan loop, persists snapshots/windows/health, generates summaries, and sends Telegram notifications. |
| Hourly / daily / cumulative summary timers | `ArbiScan.Scanner/ScannerWorker.cs` | Internal scheduler logic inside the worker for summary cutoffs and periodic heartbeat cadence. |

## C. Runtime Orchestration / Execution Flow

| Class / Component | File path | Purpose |
| --- | --- | --- |
| Market snapshot builder and health transition flow | `ArbiScan.Scanner/ScannerWorker.cs` | Pulls exchange snapshots from adapters, computes health flags, tracks stale/out-of-sync transitions, and gates downstream evaluation. |
| `OpportunityLifetimeTracker` | `ArbiScan.Core/Services/OpportunityLifetimeTracker.cs` | Maintains active profitable windows keyed by direction/notional, closes them when edge disappears, and flushes them on shutdown. |
| `ArbitrageDirectionExtensions` | `ArbiScan.Core/Services/ArbitrageDirectionExtensions.cs` | Maps logical arbitrage direction to buy/sell exchange identities used throughout evaluation and reporting. |

## D. Domain Logic / Strategies / Engines

| Class / Component | File path | Purpose |
| --- | --- | --- |
| `OpportunityDetector` | `ArbiScan.Core/Services/OpportunityDetector.cs` | Core decision engine. Evaluates both arbitrage directions for each test notional, computes optimistic/conservative outcomes, rejects degraded data, and returns edge/fee/buffer results. |
| `FillableSizeCalculator` | `ArbiScan.Core/Services/FillableSizeCalculator.cs` | Sweeps order book depth by quote or base amount to estimate executable quantity and fillability. |
| `FeeCalculator` | `ArbiScan.Core/Services/FeeCalculator.cs` | Applies exchange-specific taker fees to estimated legs. |
| `SymbolRulesNormalizer` | `ArbiScan.Core/Services/SymbolRulesNormalizer.cs` | Rounds executable quantity to both exchanges' constraints and checks minimums. |
| `QuoteStalenessTracker` | `ArbiScan.Core/Services/QuoteStalenessTracker.cs` | Tracks continuous stale-duration per exchange so stale health is confirmed over time instead of firing on a single threshold crossing. |
| Domain records | `ArbiScan.Core/Models/*.cs` | Canonical runtime/domain payloads for market snapshots, evaluations, windows, summaries, health events, and order book snapshots. |

## E. Market / Exchange / Integration / External Gateway Layer

| Class / Component | File path | Purpose |
| --- | --- | --- |
| `IExchangeAdapter` | `ArbiScan.Core/Interfaces/IExchangeAdapter.cs` | Exchange abstraction consumed by the scanner for init/start/stop/snapshot retrieval. |
| `BinanceSpotExchangeAdapter` | `ArbiScan.Exchanges.Binance/BinanceSpotExchangeAdapter.cs` | Builds Binance REST/socket clients, resolves symbol rules, maintains a local Binance spot order book, and exposes normalized snapshots. |
| `BybitSpotExchangeAdapter` | `ArbiScan.Exchanges.Bybit/BybitSpotExchangeAdapter.cs` | Builds Bybit REST/socket clients, resolves symbol rules, subscribes to supported Bybit spot order book depths, and exposes normalized snapshots. |
| `TelegramBotNotifier` | `ArbiScan.Scanner/TelegramBotNotifier.cs` | Real Telegram integration used for startup/shutdown/critical/health/heartbeat notifications. |
| `NullTelegramNotifier` | `ArbiScan.Infrastructure/Reporting/NullTelegramNotifier.cs` | Disabled notifier implementation used when Telegram is turned off. |

## F. Persistence / Storage / Schema

| Class / Component | File path | Purpose |
| --- | --- | --- |
| `IOpportunityRepository` | `ArbiScan.Core/Interfaces/IOpportunityRepository.cs` | Persistence abstraction for order book snapshots, health events, closed windows, and summary reports. |
| `SqliteOpportunityRepository` | `ArbiScan.Infrastructure/Persistence/SqliteOpportunityRepository.cs` | SQLite-backed repository. Creates schema inline, persists review-relevant runtime artifacts, and reads windows/health for summary generation. |
| Inline SQLite schema | `ArbiScan.Infrastructure/Persistence/SqliteOpportunityRepository.cs` | Defines the four persisted tables: `orderbook_snapshots`, `health_events`, `window_events`, `summary_reports`. No separate migrations or ORM context exist in this repository. |
| `IReportExporter` / `JsonReportExporter` | `ArbiScan.Core/Interfaces/IReportExporter.cs`, `ArbiScan.Infrastructure/Reporting/JsonReportExporter.cs` | Writes JSON/JSONL exports for the same persisted artifacts to mounted report storage. |

## G. Runtime State / Status / Snapshot / Diagnostics

| Class / Component | File path | Purpose |
| --- | --- | --- |
| Health state tracking | `ArbiScan.Scanner/ScannerWorker.cs` | Emits `ApplicationStarted`, status transitions, reconnect/resync, stale detection/recovery, and overall health changes. |
| `RollingFileLoggerProvider` | `ArbiScan.Infrastructure/Logging/RollingFileLoggerProvider.cs` | Writes application logs to daily rotating files under mounted storage. |
| Heartbeat message builder | `ArbiScan.Scanner/ScannerWorker.cs` | Emits periodic diagnostic Telegram heartbeat with exchange status, data age, top of book, and uptime. |
| `SummaryGenerator` | `ArbiScan.Core/Services/SummaryGenerator.cs` | Aggregates persisted windows and health events into hourly/daily/cumulative summary diagnostics. |

## H. Tests

| Class / Component | File path | Purpose |
| --- | --- | --- |
| `CoreMathTests` | `ArbiScan.Tests/CoreMathTests.cs` | Coverage for fee calculation, quantity normalization, fillability sweeping, opportunity evaluation, lifetime tracking, and summary aggregation. |
| `QuoteStalenessTrackerTests` | `ArbiScan.Tests/QuoteStalenessTrackerTests.cs` | Regression coverage for continuous stale confirmation, reset-on-fresh-update behavior, and explicit tracker resets. |

## Review Notes

- There is one runtime process and one worker: `ArbiScan.Scanner/ScannerWorker.cs`.
- There are no migrations, `DbContext`, ORM entities, queue consumers, or separate scheduler services in the current repository state.
- Persistence schema is owned directly by `SqliteOpportunityRepository` and should be reviewed there, not in a separate schema folder.
- Deployment wiring spans repo files and mounted VPS config:
  `docker-compose.yml`, `Dockerfile`, `config/appsettings.example.json`, `config/telegramsettings.example.json`, `.env.example`.
