# ArbiScan

`ArbiScan` is a .NET 10 scanner for conservative spot arbitrage analysis between `Binance Spot` and `Bybit Spot` for exactly one symbol per run.

It does not place orders. The service builds local order books, evaluates both directions (`Buy Binance / Sell Bybit` and `Buy Bybit / Sell Binance`), estimates gross/net edge with fees and buffers, tracks opportunity lifetimes, persists analytics to SQLite, and exports machine-readable reports to local storage on the VPS.

## What Is Implemented

- .NET 10 solution split into `Core`, `Infrastructure`, `Exchanges.Binance`, `Exchanges.Bybit`, `Scanner`, `Tests`
- Local Binance and Bybit spot order books built from `Binance.Net` and `Bybit.Net`
- Configurable one-symbol scan loop with configurable depth and test notionals
- Conservative and optimistic evaluation modes
- Fee, fillability, rounding and safety-buffer calculations
- Opportunity lifetime tracking and window persistence
- SQLite persistence for snapshots, health events, windows and summaries
- JSON/JSONL report exports
- File logging to local VPS-mounted storage
- Dockerfile, compose config and VPS run instructions
- Unit tests for key math and aggregation logic

## Current Stack

- App version: auto-generated as `1.0.<git-commit-count>`
- .NET SDK/runtime: `10.0.104 / 10.0.4`
- `Binance.Net`: `12.11.1`
- `Bybit.Net`: `6.10.0`
- `Microsoft.Data.Sqlite`: `10.0.0`

## Project Layout

- `ArbiScan.Core`: domain models, interfaces, calculations, summaries
- `ArbiScan.Infrastructure`: SQLite repository, JSON exporters, rolling file logger
- `ArbiScan.Exchanges.Binance`: Binance spot metadata and local order book adapter
- `ArbiScan.Exchanges.Bybit`: Bybit spot metadata and local order book adapter
- `ArbiScan.Scanner`: generic host, orchestration loop, health tracking
- `ArbiScan.Tests`: unit tests for math and summary aggregation

## Storage Layout On VPS

The container writes everything under one mounted root, default `/app/storage`.

Host-side recommendation:

- `/srv/ArbiScan/config`
- `/srv/ArbiScan/logs`
- `/srv/ArbiScan/data`
- `/srv/ArbiScan/reports`

Mounted into container:

- `/app/storage/config`
- `/app/storage/logs`
- `/app/storage/data`
- `/app/storage/reports`

Generated artifacts:

- `data/arbiscan.sqlite`: primary SQLite database
- `logs/application-YYYYMMDD.log`: rolling application log
- `reports/orderbook-snapshots-YYYYMMDD.jsonl`: periodic raw order book snapshots
- `reports/health-events-YYYYMMDD.jsonl`: health and degraded-state events
- `reports/window-events-YYYYMMDD.jsonl`: closed opportunity windows
- `reports/summaries-YYYYMMDD.jsonl`: machine-readable summaries
- `reports/hourly-*.json`, `daily-*.json`, `cumulative-*.json`: summary snapshots

## Configuration

Base settings live in:

- `ArbiScan.Scanner/appsettings.json`
- `config/appsettings.example.json`
- `config/telegramsettings.example.json`

Production override file should be placed in the mounted config folder:

- `/srv/ArbiScan/config/appsettings.json`
- `/srv/ArbiScan/config/telegramsettings.json`

Secrets should come from environment variables or a private config override:

- `ArbiScan__Binance__ApiKey`
- `ArbiScan__Binance__ApiSecret`
- `ArbiScan__Bybit__ApiKey`
- `ArbiScan__Bybit__ApiSecret`
- `TelegramBot__BotToken`
- `ARBISCAN_IMAGE`

## Telegram Notifications

Telegram uses a separate config file and works with exactly one allowed user/chat id.

Config file:

- `/srv/ArbiScan/config/telegramsettings.json`

Fields:

- `Enabled`
- `BotToken`
- `AllowedUserId`
- `HeartbeatIntervalMinutes`
- `NotifyOnStartup`
- `NotifyOnShutdown`
- `NotifyOnCriticalError`
- `NotifyOnHealthStateChanges`

Notifications sent:

- bot startup
- bot shutdown with probable reason
- critical errors in short form
- health state transitions
- periodic heartbeat with symbol, health, order book status, best bid/ask, data age and closed window count

## Reports

### Window Events Report

Each closed window export includes:

- timestamps
- direction and exchanges
- Binance/Bybit best bid and ask
- test notional
- fillability classification
- executable quantity
- gross pnl
- optimistic net pnl
- conservative net pnl
- fees
- buffers
- lifetime
- health flags

### Summary Reports

Hourly, daily and cumulative summaries include:

- window counts by direction and notional
- positive gross/net counts
- lifetime distribution
- pnl distributions
- fee and buffer totals
- fillability counts
- health/reconnect/resync/stale counters
- healthy vs degraded duration
- computed final assessment text

## Build And Test

```bash
dotnet build ArbiScan.slnx
dotnet test ArbiScan.slnx
```

## Local Smoke Run

```bash
ArbiScan__Storage__RootPath=/tmp/arbiscan-smoke \
dotnet run --project ArbiScan.Scanner
```

## Docker Run

Prepare host directories:

```bash
mkdir -p /srv/ArbiScan/config /srv/ArbiScan/logs /srv/ArbiScan/data /srv/ArbiScan/reports
cp config/appsettings.example.json /srv/ArbiScan/config/appsettings.json
cp config/telegramsettings.example.json /srv/ArbiScan/config/telegramsettings.json
cp .env.example .env
```

Build and start:

```bash
chmod +x ./scripts/get-version.sh
docker build --build-arg APP_VERSION="$(./scripts/get-version.sh)" -t arbiscan:latest .
docker compose up -d
```

Stop:

```bash
docker compose down
```

## VPS Deployment Notes

1. Push code to GitHub and let GitHub Actions publish `ghcr.io/dvpot/arbiscan`.
2. Make sure the host directories under `/srv/ArbiScan` exist before first start.
3. Set `ARBISCAN_IMAGE=ghcr.io/dvpot/arbiscan:latest` in `.env`.
4. Put production config into `/srv/ArbiScan/config/appsettings.json`.
5. Put API secrets into `.env` or environment variables.
6. Pull and start with `docker compose pull && docker compose up -d`.
7. Inspect logs with `docker compose logs -f arbiscan`.

## GitHub Registry Deployment

The repository includes a GitHub Actions workflow that:

- builds and tests on pushes and pull requests;
- publishes a Docker image to `GHCR` on pushes to `main` or `master`;
- tags images as `<version>`, `latest` and `sha-<commit>`.

Versioning is managed in:

- `scripts/get-version.sh`

The bot version is generated automatically from git history as `1.0.<commit-count>`. Every new commit increments the version without manual edits. The Docker image bakes that value into `ARBISCAN_VERSION`, the bot uses it in Telegram notifications, and the GitHub workflow uses it for image tags.

Example VPS `.env`:

```bash
ARBISCAN_IMAGE=ghcr.io/dvpot/arbiscan:latest
ARBISCAN_STORAGE_ROOT=/srv/ArbiScan
ARBISCAN_BINANCE_API_KEY=...
ARBISCAN_BINANCE_API_SECRET=...
ARBISCAN_BYBIT_API_KEY=...
ARBISCAN_BYBIT_API_SECRET=...
TELEGRAM_BOT_TOKEN=...
```

Update on VPS:

```bash
cd /opt/ArbiScan
sudo docker compose pull
sudo docker compose up -d
```

## Constraints And Assumptions

- Only one spot symbol is scanned per process.
- Only Binance Spot and Bybit Spot are supported.
- No order placement, balances, live trading or rebalancing are implemented.
- Calculations rely on live local order books and current top/depth liquidity.
- Conservative mode applies configurable latency, slippage and additional safety buffers.
- Bybit spot websocket supports discrete subscription depths; the adapter subscribes to the nearest supported depth and then trims output to the configured logical depth.
- Health events are persisted locally and degrade decisions when books are stale or unsynced.

## Verification Performed

- `dotnet build ArbiScan.slnx`
- `dotnet test ArbiScan.slnx`
- smoke run with local storage root under `/tmp/arbiscan-smoke`
