# ArbiScan v2 Lite

`ArbiScan` v2 is a .NET 10 scanner for one-symbol spot arbitrage observation between `Binance Spot` and `Bybit Spot`.

It does not trade. It watches only best bid / best ask, evaluates both directions for small test notionals, groups positive observations into windows, stores machine-readable artifacts locally, and sends optional Telegram notifications.

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
- `Scanner`: host bootstrap, worker loop, Telegram notifier
- `Tests`: v2-lite unit tests
- `config`: example runtime config files
- `scripts`: versioning, bundle collection, VPS prep helpers

## Runtime Storage

Recommended VPS root:

- `/srv/arbiscan-v2/config`
- `/srv/arbiscan-v2/logs`
- `/srv/arbiscan-v2/data`
- `/srv/arbiscan-v2/reports`

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

- `/srv/arbiscan-v2/config/appsettings.json`
- `/srv/arbiscan-v2/config/telegramsettings.json`

Important app settings:

- `Symbol`
- `BaseAsset`
- `QuoteAsset`
- `RuntimeMode`
- `ScanIntervalMs`
- `QuoteStalenessThresholdMs`
- `TestNotionalsUsd`
- `BinanceTakerFeeRate`
- `BybitTakerFeeRate`
- `SafetyBufferBps`
- `EntryThresholdUsd`
- `EntryThresholdBps`
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

## Build And Test

```bash
dotnet build ArbiScan.slnx
dotnet test ArbiScan.slnx
```

## Local Run

`Program.cs` loads the tracked example config first, then overrides it from `/app/storage/config` (or `ArbiScan__Storage__RootPath`), so local runs work without `Scanner/appsettings.json`.

Example:

```bash
ArbiScan__Storage__RootPath=/tmp/arbiscan-v2 \
dotnet run --project Scanner/ArbiScan.Scanner.csproj
```

## Docker And VPS

Prepare storage:

```bash
mkdir -p /srv/arbiscan-v2/config /srv/arbiscan-v2/logs /srv/arbiscan-v2/data /srv/arbiscan-v2/reports
cp config/appsettings.example.json /srv/arbiscan-v2/config/appsettings.json
cp config/telegramsettings.example.json /srv/arbiscan-v2/config/telegramsettings.json
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
2. Keep Telegram config, but move/update it for v2.
3. Replace app settings with the v2 structure.
4. Delete old runtime logs/data/reports before the first v2 start.

Helper:

```bash
./scripts/prepare-vps-v2-storage.sh /srv/ArbiScan /srv/arbiscan-v2
```

This helper:

- creates the v2 storage tree
- preserves config backups
- carries `telegramsettings.json` forward when present
- seeds `appsettings.json` from the tracked v2 example when needed
- clears `logs`, `data`, and `reports` in the v2 storage root

## Analysis Bundle

```bash
./scripts/collect-analysis-bundle.sh 20260405 /srv/arbiscan-v2
```
