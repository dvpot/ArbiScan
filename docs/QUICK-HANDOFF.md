# ArbiScan Quick Handoff

Last updated: 2026-04-02
Repository: `/home/dvpot/projects/github/ArbiScan`
Primary task tracker: `/home/dvpot/projects/Tasks/ArbiScan/arbiscan_feedback_codex_ru_2026-04-02.md`

## 1. Project Purpose

`ArbiScan` is a scanner-only .NET 10 service for one-symbol spot arbitrage analysis between Binance Spot and Bybit Spot.
Out of scope by design:

- live trading
- order placement
- balance management
- multi-symbol orchestration

## 2. Repo And Delivery Flow

- Default branch: `main`
- Git remote: `origin -> git@github.com:dvpot/ArbiScan.git`
- Container image target: `ghcr.io/dvpot/arbiscan:latest`
- Expected VPS app directory: `/opt/ArbiScan`
- Expected VPS storage root: `/srv/ArbiScan`
- Review docs: `docs/review/`

Normal release flow:

1. Make code changes in this repo.
2. Run local verification:
   `dotnet build Scanner/ArbiScan.Scanner.csproj`
   `dotnet test`
3. Commit on `main`.
4. `git push origin main`
5. GitHub Actions builds/tests and publishes GHCR image.
6. GitHub Actions deploys to VPS automatically over SSH when these repo secrets are configured:
   `ARBISCAN_VPS_HOST`
   `ARBISCAN_VPS_USER`
   `ARBISCAN_VPS_SSH_KEY`
   `ARBISCAN_VPS_APP_DIR`
7. Deploy command executed by CI on VPS:
   `cd <ARBISCAN_VPS_APP_DIR> && docker compose pull && docker compose up -d`
8. If secrets are missing, publish still succeeds and deploy is skipped with a workflow summary note.
9. Check runtime:
   `sudo docker compose logs -f arbiscan`

Manual VPS deploy is now fallback only, not the default flow.

GitHub content hygiene:

- `.env` is ignored; only `.env.example` is tracked.
- Local runtime bundles under `analysis-bundles/` are ignored.
- `Scanner/Properties/launchSettings.json` remains tracked intentionally for local `dotnet run` convenience.

## 3. Runtime Paths On VPS

- Config: `/srv/ArbiScan/config`
- Logs: `/srv/ArbiScan/logs`
- Data: `/srv/ArbiScan/data`
- Reports: `/srv/ArbiScan/reports`

Most important runtime artifacts:

- `/srv/ArbiScan/logs/application-YYYYMMDD.log`
- `/srv/ArbiScan/reports/orderbook-snapshots-YYYYMMDD.jsonl`
- `/srv/ArbiScan/reports/health-events-YYYYMMDD.jsonl`
- `/srv/ArbiScan/reports/window-events-YYYYMMDD.jsonl`
- `/srv/ArbiScan/reports/summaries-YYYYMMDD.jsonl`
- `/srv/ArbiScan/reports/hourly-*.json`
- `/srv/ArbiScan/reports/daily-*.json`
- `/srv/ArbiScan/reports/cumulative-*.json`
- `/srv/ArbiScan/reports/health-hourly-*.json`
- `/srv/ArbiScan/reports/health-daily-*.json`
- `/srv/ArbiScan/reports/health-cumulative-*.json`

## 4. What Was Completed In The Latest Pass

Feedback source:
`/home/dvpot/projects/Tasks/ArbiScan/arbiscan_feedback_codex_ru_2026-04-02.md`

Implemented:

- Stateful stale confirmation was kept and extended with richer runtime diagnostics.
- Binance and Bybit adapters now capture `LastUpdateCallbackUtc`.
- Worker now logs stale transitions with:
  `OrderBook.Status`, `UpdateTime`, `UpdateServerTime`, `LastUpdateCallbackUtc`, `DataAge`, best bid/ask, and bid/ask depth counts.
- Worker now logs threshold crossings for `DataAge > 1000ms`, `> 2000ms`, and `> QuoteStalenessThresholdMs`.
- Telegram health notifications now support debounce/cooldown:
  `HealthStateChangeMinNotifyIntervalSeconds`
  `RequireStableHealthyBeforeNotifyMs`
  `RequireStableDegradedBeforeNotifyMs`
- Heartbeat messages now include cumulative stale/reconnect/resync counters.
- Added dedicated aggregated `HealthReport` generation and export:
  `health-hourly-*.json`
  `health-daily-*.json`
  `health-cumulative-*.json`
- Added tests for health report aggregation.
- Review artifacts (`00-*`, `01-*`, `02-*`) were trimmed and updated to reflect the runtime changes more clearly.
- Repository structure was simplified:
  project folders no longer carry the `ArbiScan.` prefix at root level.

## 5. Current Priorities / Open Questions

1. Validate on real runtime data whether `BybitStale` is caused by websocket gaps, reconnect/resync, timestamp semantics, or local processing lag.
2. Review the new logs and health reports from VPS after deployment.
3. Confirm whether degraded periods are suppressing otherwise interesting windows in production.
4. Keep scope strict: scanner-only, no live trading additions.

## 6. How To Collect Data For Next Analysis

Preferred one-command collection from repo root:

```bash
./scripts/collect-analysis-bundle.sh 20260402 /srv/ArbiScan
```

The script prepares a sanitized bundle with:

- `application-YYYYMMDD.log`
- `health-events-YYYYMMDD.jsonl`
- `orderbook-snapshots-YYYYMMDD.jsonl`
- `window-events-YYYYMMDD.jsonl`
- latest `hourly`, `daily`, `cumulative` summary json
- latest `health-hourly`, `health-daily`, `health-cumulative` json
- sanitized production `appsettings.json`
- a grep-based stale diagnostics excerpt file
- `tar.gz` archive for transfer

If manual collection is needed, send:

1. `logs/application-YYYYMMDD.log`
2. `reports/health-events-YYYYMMDD.jsonl`
3. `reports/orderbook-snapshots-YYYYMMDD.jsonl`
4. `reports/window-events-YYYYMMDD.jsonl`
5. latest `hourly-*.json` and `cumulative-*.json`
6. current production `appsettings.json` without secrets
7. log fragment around stale event time, minimum plus/minus 2 minutes

## 7. Fast Start For A New Chat

When starting a fresh chat, first read:

- `/home/dvpot/projects/github/ArbiScan/docs/QUICK-HANDOFF.md`
- `/home/dvpot/projects/github/ArbiScan/docs/review/00-repo-tree.txt`
- `/home/dvpot/projects/github/ArbiScan/docs/review/01-critical-files-map.md`
- `/home/dvpot/projects/github/ArbiScan/docs/review/02-review-delta.md`
- `/home/dvpot/projects/Tasks/ArbiScan/arbiscan_feedback_codex_ru_2026-04-02.md`
- `git status --short --branch`

Then continue from the latest open priority instead of re-discovering repo context.
