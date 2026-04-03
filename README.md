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

- `Core`: domain models, interfaces, calculations, summaries
- `Infrastructure`: SQLite repository, JSON exporters, rolling file logger
- `Exchanges.Binance`: Binance spot metadata and local order book adapter
- `Exchanges.Bybit`: Bybit spot metadata and local order book adapter
- `Scanner`: generic host, orchestration loop, health tracking
- `Tests`: unit tests for math and summary aggregation
- `docs/review`: external technical review navigation artifacts

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
- `reports/stale-diagnostics-YYYYMMDD.jsonl`: per-stale/per-recovery diagnostics
- `reports/rejected-positive-signals-YYYYMMDD.jsonl`: raw-positive signals rejected before windowing
- `reports/window-events-YYYYMMDD.jsonl`: closed opportunity windows
- `reports/summaries-YYYYMMDD.jsonl`: machine-readable summaries
- `reports/hourly-*.json`, `daily-*.json`, `cumulative-*.json`: summary snapshots
- `reports/health-hourly-*.json`, `health-daily-*.json`, `health-cumulative-*.json`: aggregated health-only reports

## Configuration

Base settings live in:

- `Scanner/appsettings.json`
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
- `HealthStateChangeMinNotifyIntervalSeconds`
- `RequireStableHealthyBeforeNotifyMs`
- `RequireStableDegradedBeforeNotifyMs`

Notifications sent:

- bot startup
- bot shutdown with probable reason
- critical errors in short form
- debounced health state transitions with exchange-level status/data-age context
- periodic heartbeat with symbol, health, callback/update timestamps, data age, closed window count, stale count, reconnect count and resync count

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
- raw-positive cross counts by direction
- reject reasons by direction, by notional, and by direction+notional
- positive gross/net counts
- lifetime distribution
- pnl distributions
- fee and buffer totals
- fillability counts
- health/reconnect/resync/stale counters
- healthy vs degraded duration
- computed final assessment text

### Health Reports

Dedicated health reports include:

- reconnect count by exchange
- resync count by exchange
- stale count by exchange
- stale detected/recovered/flap counts by exchange
- stale/data-age/callback-silence aggregates by exchange
- rough stale root-cause classification by exchange
- healthy vs degraded duration
- top degradation causes
- longest stale interval by exchange
- last stale / reconnect / resync timestamps by exchange

## Build And Test

```bash
dotnet build ArbiScan.slnx
dotnet test ArbiScan.slnx
```

## Local Smoke Run

```bash
ArbiScan__Storage__RootPath=/tmp/arbiscan-smoke \
dotnet run --project Scanner/ArbiScan.Scanner.csproj
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

1. Push code to GitHub and let GitHub Actions build, test, publish `ghcr.io/<owner>/arbiscan`, then auto-deploy on VPS over SSH.
2. Make sure the host directories under `/srv/ArbiScan` exist before first start.
3. Set `ARBISCAN_IMAGE=ghcr.io/<owner>/arbiscan:latest` in `.env`.
4. Put production config into `/srv/ArbiScan/config/appsettings.json`.
5. Put API secrets into `.env` or environment variables.
6. Configure GitHub Actions repository secrets:
   `ARBISCAN_VPS_HOST`, `ARBISCAN_VPS_USER`, `ARBISCAN_VPS_SSH_KEY`, `ARBISCAN_VPS_APP_DIR`
7. The deploy job runs:
   `cd <ARBISCAN_VPS_APP_DIR> && docker compose pull && docker compose up -d`
8. If those secrets are missing, image publish still works and deploy is skipped with a visible workflow summary note.
9. Inspect runtime with `docker compose logs -f arbiscan`.

## GitHub Registry Deployment

The repository includes a GitHub Actions workflow that:

- builds and tests on pushes and pull requests;
- publishes a Docker image to `GHCR` on pushes to `main` or `master`;
- deploys the freshly published image to VPS on pushes to `main` or `master` when VPS secrets are configured;
- tags images as `<version>`, `latest` and `sha-<commit>`.

Versioning is managed in:

- `scripts/get-version.sh`

The bot version is generated automatically from git history as `1.0.<commit-count>`. Every new commit increments the version without manual edits. The Docker image bakes that value into `ARBISCAN_VERSION`, the bot uses it in Telegram notifications, and the GitHub workflow uses it for image tags.

Example VPS `.env`:

```bash
ARBISCAN_IMAGE=ghcr.io/<owner>/arbiscan:latest
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

Collect runtime artifacts for follow-up analysis:

```bash
./scripts/collect-analysis-bundle.sh 20260402 /srv/ArbiScan
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
