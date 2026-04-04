# Follow-Up Delta: 2026-04-04

This note captures the code changes made in response to the latest feedback about `XRPUSDT` raw-positive candidates being rejected before any windows were opened.

## What Changed

- Added structured fillability decision payloads via `FillabilityDecisionDetails`:
  - requested quote notional;
  - required base quantity before rounding;
  - top-of-book quantities on buy/sell sides;
  - aggregated buy/sell fillable quantities;
  - quantity before rounding and after Binance/Bybit rounding;
  - effective executable quantity and depth-consumption details;
  - machine-readable `decisionCode` plus human-readable summary/detail.
- Extended `OpportunityDetector` to compute the fillability payload for every evaluation and attach it to `OpportunityPairEvaluation`.
- Extended runtime evaluation telemetry with:
  - `primaryRejectReason`;
  - `secondaryRejectReasons`;
  - `wouldBeProfitableWithoutFees`;
  - `wouldBeProfitableWithoutFillability`;
  - net edge USD / pct;
  - embedded fillability payload.
- Added sampled `candidate-rejections-YYYYMMDD.jsonl` export:
  - emitted only for raw-positive rejected candidates;
  - capped to the first `20` samples per UTC hour per primary reject reason;
  - includes the minimum fields requested in the 2026-04-04 feedback.
- Extended summary debug statistics with:
  - `primaryRejectReasonCounts`;
  - `secondaryRejectReasonCounts`;
  - `rejectedDueToFeesAndFillabilityCount`;
  - `wouldBeProfitableWithoutFeesCount`;
  - `wouldBeProfitableWithoutFillabilityCount`.
- Added standalone `fillability-diagnostics-<period>-*.json` exports and embedded fillability diagnostics blocks inside the normal summary JSON:
  - fillable / partially fillable / not fillable counts by notional;
  - average required quantity by notional;
  - average top1 and aggregated topN available quantities;
  - median topN available quantity;
  - average rounded executable quantity;
  - top non-fillability decision codes overall and by notional.
- Expanded tests with XRP-style depth/rounding cases for small notionals (`10`, `20`, `50`) and summary assertions for the new reject/fillability metrics.
- Updated `collect-analysis-bundle.sh` to collect:
  - `candidate-rejections-*.jsonl`;
  - `fillability-diagnostics-hourly-*.json`;
  - `fillability-diagnostics-daily-*.json`;
  - `fillability-diagnostics-cumulative-*.json`.

## Intended Outcome

- Make it possible to inspect whether `fillability` rejections on `XRPUSDT` are caused by real market depth/rounding constraints or by logic/accounting mistakes.
- Separate the first-order rejection cause from overlapping secondary causes.
- Quantify how many raw-positive candidates are being blocked specifically by fees versus fillability.

## Remaining Limitation

The new exports and summary blocks are implemented and locally verified, but fresh production evidence still requires:

1. deploy the updated scanner;
2. let it run long enough to produce new runtime artifacts;
3. collect a fresh VPS analysis bundle for external review.
