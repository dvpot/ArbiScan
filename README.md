# ArbiScan v2 Lite

`ArbiScan` v2 is a .NET 10 scanner for one-symbol spot arbitrage observation between `Binance Spot` and `Bybit Spot`.

It does not trade. It watches only best bid / best ask, evaluates both directions for small test notionals, groups positive observations into windows, stores machine-readable artifacts locally, and sends optional Telegram notifications and control commands.

## Scope

- one symbol per process
- Binance Spot + Bybit Spot only
- best bid / best ask only
- fees + safety buffer + entry thresholds
- raw/fee/net/entry-qualified signal stages
- window statistics
- hourly / daily / cumulative summary reports
- practical health events and health summaries

Not included:

- deep order book analytics
- fillability engine
- execution simulator
- live trading
- multi-symbol orchestration

## Repo Layout

- `Core`: domain models, enums, calculators, summary/health generators
- `Infrastructure`: SQLite persistence, JSON/JSONL exporters, rolling file logging
- `Exchanges.Binance`: Binance best-bid-ask adapter
- `Exchanges.Bybit`: Bybit top-of-book adapter
- `Scanner`: host bootstrap, worker loop, Telegram notifier and control bot
- `Tests`: v2-lite unit tests
- `config`: example runtime config files
- `scripts`: versioning, bundle collection, VPS prep helpers

## Runtime Storage

Recommended VPS root:

- `/srv/ArbiScan/config`
- `/srv/ArbiScan/logs`
- `/srv/ArbiScan/data`
- `/srv/ArbiScan/reports`

Main artifacts:

- `data/arbiscan.sqlite`
- `logs/application-YYYYMMDD.log`
- `reports/raw-signal-events-YYYYMMDD.jsonl`
- `reports/window-events-YYYYMMDD.jsonl`
- `reports/health-events-YYYYMMDD.jsonl`
- `reports/summaries-YYYYMMDD.jsonl`
- `reports/health-summaries-YYYYMMDD.jsonl`
- `reports/hourly-*.json`
- `reports/daily-*.json`
- `reports/cumulative-*.json`
- `reports/health-hourly-*.json`
- `reports/health-daily-*.json`
- `reports/health-cumulative-*.json`

## Configuration

Tracked examples:

- `config/appsettings.example.json`
- `config/telegramsettings.example.json`
- `.env.example`

Production files live in the mounted VPS config directory:

- `/srv/ArbiScan/config/appsettings.json`
- `/srv/ArbiScan/config/telegramsettings.json`
- `/srv/ArbiScan/config/appsettings/*.json`

Important app settings:

- `Symbol`
- `BaseAsset`
- `QuoteAsset`
- `ScanIntervalMs`
- `QuoteStalenessThresholdMs`
- `TestNotionalsUsd`
- `BinanceTakerFeeRate`
- `BybitTakerFeeRate`
- `SafetyBufferBps`
- `EntryThresholdUsd`
- `EntryThresholdBps`
- `RawSignalJsonExportMode`
- `Storage`

Telegram settings:

- `Enabled`
- `BotToken`
- `AllowedUserId`
- `HeartbeatIntervalMinutes`
- `NotifyOnStartup`
- `NotifyOnShutdown`
- `NotifyOnCriticalError`
- `NotifyOnHealthStateChanges`
- `NotifyOnSignalLifecycle`
- `NotifyOnSignalNewMax`

Notes:

- API keys are not required for the normal public-data scanner run.
- `RawSignalJsonExportMode` controls JSONL noise while SQLite still keeps the full raw history.
- `Bybit.Net 6.10.0` spot ticker stream does not expose best bid / ask, so the current lightweight `BybitSymbolOrderBook(limit=1)` remains the practical top-of-book choice.
- Telegram control commands are allowed only for `TelegramBot:AllowedUserId`.
- Appsettings preset list is stored in `/srv/ArbiScan/config/appsettings`.

Telegram control commands:

- `/status`
- `/settings`
- `/presets`
- `/set <path> <json-value>`
- `/save_preset <name>`
- `/use_preset <name>`
- `/upsert_preset <name>`
- `/restart`

## Build And Test

```bash
dotnet build ArbiScan.slnx
dotnet test ArbiScan.slnx
```

## Local Run

`Program.cs` loads the tracked example config first, then overrides it from `/app/storage/config` (or `ArbiScan__Storage__RootPath`), so local runs work without `Scanner/appsettings.json`.

Example:

```bash
ArbiScan__Storage__RootPath=/tmp/arbiscan \
dotnet run --project Scanner/ArbiScan.Scanner.csproj
```

## Docker And VPS

Prepare storage:

```bash
mkdir -p /srv/ArbiScan/config /srv/ArbiScan/logs /srv/ArbiScan/data /srv/ArbiScan/reports
cp config/appsettings.example.json /srv/ArbiScan/config/appsettings.json
cp config/telegramsettings.example.json /srv/ArbiScan/config/telegramsettings.json
cp .env.example .env
```

Build and start:

```bash
chmod +x ./scripts/get-version.sh
docker build --build-arg APP_VERSION=\"$(./scripts/get-version.sh)\" -t arbiscan:latest .
docker compose up -d
```

The GitHub Actions workflow builds, tests, publishes `ghcr.io/<owner>/arbiscan`, then deploys to the configured VPS app directory. During deploy it ensures the v2 storage directories exist before `docker compose up -d`.

## VPS Migration To v2

Before the first v2 launch on VPS:

1. Backup or stop the current container.
2. Keep Telegram config and update app settings to the v2 structure.
3. Replace app settings with the v2 structure.
4. Delete old runtime logs/data/reports before the first v2 start.

Helper:

```bash
./scripts/prepare-vps-storage.sh /srv/ArbiScan /srv/ArbiScan
```

This helper:

- keeps the normal `ArbiScan` storage tree
- preserves config backups
- carries `telegramsettings.json` forward when present
- seeds `appsettings.json` from the tracked v2 example when needed
- clears `logs`, `data`, and `reports` before the first v2 start

## Analysis Bundle

```bash
./scripts/collect-analysis-bundle.sh 20260405 /srv/ArbiScan
```
