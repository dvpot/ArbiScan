# Follow-Up Delta: 2026-04-03

This note captures the code changes made in response to the follow-up feedback on `BybitStale` diagnostics and summary explainability.

## What Changed

- Added structured stale diagnostics export to `reports/stale-diagnostics-YYYYMMDD.jsonl`.
- Enriched `OrderBookSnapshot` with:
  - `DataAgeByExchangeTimestamp`
  - `DataAgeByLocalCallbackTimestamp`
  - `LastTopOfBookChangeUtc`
  - `TimeSinceTopOfBookChanged`
- Updated Binance and Bybit adapters to:
  - track `lastSuccessfulCallbackUtc` on every order-book callback;
  - track top-of-book changes separately from callback arrivals;
  - expose both exchange-timestamp age and local-callback age.
- Extended `ScannerWorker` to:
  - emit per-exchange `stale_detected` / `stale_recovered` diagnostics;
  - classify a rough `staleLikelyRootCause`;
  - record loop timing and degraded flags in stale diagnostics;
  - accumulate runtime evaluation telemetry for rejection reasons.
- Extended summary output with debug counters:
  - `rawPositiveCrossCount`
  - `rejectedDueToFeesCount`
  - `rejectedDueToBuffersCount`
  - `rejectedDueToHealthCount`
  - `rejectedDueToMinLifetimeCount`
  - `rejectedDueToFillabilityCount`
  - `rejectedDueToRulesCount`
- Extended health reports with:
  - stale detected / recovered / flap counts by exchange;
  - stale duration aggregates;
  - data-age aggregates;
  - callback-silence aggregates;
  - root-cause classification by exchange.
- Improved Telegram error logging to include exception type, message, inner exception, endpoint/method marker, and status/error code details.
- Upgraded `collect-analysis-bundle.sh` to include:
  - `stale-diagnostics-*.jsonl`
  - `health-daily-*.json` when present
  - all `application-*.log`
  - redacted `telegramsettings.json`
  - explicit missing marker for absent `window-events`
  - `runtime-meta.json`

## Bybit Adapter Timestamp Semantics

Code review result for `Exchanges.Bybit/BybitSpotExchangeAdapter.cs`:

- `lastSuccessfulCallbackUtc` is updated from `BybitSymbolOrderBook.OnOrderBookUpdate`.
- `updateTimeUtc` and `updateServerTimeUtc` come from the underlying library state exposed by `BybitSymbolOrderBook`.
- It is possible for a callback to arrive without a top-of-book change; the code now tracks this separately.
- The scanner can therefore distinguish:
  - callback silence;
  - stale exchange timestamps;
  - unchanged top of book despite callbacks.

## Remaining Limitation

The new stale diagnostics and extended health metrics require the updated scanner build to run in production before fresh runtime evidence appears in exported reports.
