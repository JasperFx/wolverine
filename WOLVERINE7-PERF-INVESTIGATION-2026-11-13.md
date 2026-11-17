# Wolverine 7 performance investigation, 2026-11-13 (evening)

Box: M5 Max, 18 cores, macOS 26.4.1. CritterStackScalability branch `perf-after-lane-alphas` (commit f547887 + lanes).
Lanes: `before` = Wolverine 6.62 / JasperFx 2.100.1 / .NET 10; `after` = Wolverine 7.0.0-alpha.2 / JasperFx 3.0.1 /
.NET 11; `net11-v6` = the before packages on .NET 11 (splits runtime from Wolverine). Every comparison is interleaved,
≥ 3 rounds, medians. Note: `BTLEServer` held one core at 100% all evening (both lanes equally).

## 1. Baseline: 6.62 vs 7.0.0-alpha.2 (3 rounds, all invoke shapes + micro)

Wolverine 7 alpha.2 is faster than 6.62 on 21 of 27 invoke shapes (16 callers), mostly +13% to +33%:
plain +19% (3.72M → 4.42M/s), async +33%, reply +26%, compound +18%, graph +19%, valuetask +22%.
Single-threaded (BenchmarkDotNet): plain 152 → 129 ns, async 177 → 128 ns, cascade 11.2 → 6.9 µs, graph 3.3 → 1.9 µs.

Apparent regressions: fanout −58%, remote-grpc −35%, remote-tcp −32%, schedule −23%, envelope serialization at 64K
+29..37%.

## 2. Where the "regressions" come from: none is Wolverine 7

| shape / benchmark | 6.62 net10 | 6.62 net11 | 7.0 net11 | verdict |
|---|---|---|---|---|
| fanout ops/s | 256k | 157k | 182k | .NET 11 runtime (−39%); Wolverine 7 is *ahead* on net11 |
| remote-grpc ops/s | 112k | 99k | 101k | .NET 11 runtime (−11%); Wolverine 7 neutral |
| schedule ops/s | 1.91M | 1.85M | 1.77M | noise (overlapping ranges) |
| remote-tcp | 8–12 req/s | | | bound by the 250 ms TCP batch timeout; noise |
| envelope/body serialization | | | | Wolverine 7 share ≈ 0 on every benchmark; 64K runs swing 2× run to run; .NET 11 adds 6–12% to small-envelope serialize/deserialize |

The fanout loss is lock contention inside .NET's multi-reader `UnboundedChannel`, which backs JasperFx `Block<T>`
for Wolverine's buffered local queues. Standalone (16 writers, 8 readers): multi-reader channel 1.2M/s on net10,
0.96M/s on net11; one multi-reader channel per reader with stealing 12M/s / 10M/s.

## 3. Experiments on local code

| experiment | change | result |
|---|---|---|
| **A** (wolverine `perf/lockfree-envelope-ids`, `7.0.0-perf.1`) | `EnvelopeIds` uses a lock-free NewId-layout generator (CAS on one tick+sequence counter) instead of `NewIdGenerator`'s SpinLock; same byte layout, process id kept (#5399) | **plain +42% (3.72M → 5.27M/s), async +56%, reply +47%, tenant +30%, graph +12%, fanout +67% (2/3 rounds), cascade +7%** — tight spreads. 19/19 envelope id tests pass |
| B (jasperfx `perf/sharded-unbounded-block`, `3.0.2-perf.1`) | unbounded multi-reader `Block` = one channel per reader, round-robin writes, stealing | send-local +67% but fanout −22%, schedule −28%, publish-local erratic. A sleeping reader only wakes for its own channel. **Negative** |
| B2 (`3.0.2-perf.2`) | unbounded multi-reader `Block` = ConcurrentQueue + idle-reader semaphore | publishers enqueue 2× faster but handled/s does not improve (backlog drains 10–24 s); fanout handled/s −30%. **Negative** — the channel lock is not what limits end-to-end throughput |
| C0 (ceiling only, `7.0.0-perf.2`, on top of A) | no per-message linked `CancellationTokenSource` + `CancelAfter` in `Executor.ExecuteAsync` (not shippable: handler timeouts stop working) | handled/s vs A: fanout 1.1–1.5M → 2.5–2.6M (~+100%), send-local +43%, queued ≈; cascade caller ops −24% |
| **C1** (wolverine `perf/exp-c1-sharded-cancellation`, `7.0.0-perf.3`, on top of A) | `ExecuteAsync` links each message's timeout source to one of N per-thread intermediate sources (each linked once to the receiver's token) instead of straight to the shared token, so the shared token's registration lock stops being contended. Same semantics: shutdown still cancels in-flight handlers, timeouts unchanged | handled/s vs A: **fanout 1.16–1.28M → 2.41–2.50M (≈ +100%), send-local 1.83 → 2.41M (+32%)**, queued +12.5%, plain +7%; cascade handled/s −7% (2.82–2.95M → 2.58–2.74M) |
| A2 (`7.0.0-perf.4`, on A) | per-thread reserved blocks of 32 id values (one CAS per block, block dropped when the clock moves past its tick) | −2% on six of seven shapes vs A: the thread-static bookkeeping costs more than the CAS it saves. **Negative** |

Measurement caveats:
- `send-local` / `publish-local` measure the *enqueue* rate into an unbounded local queue; quote handled/s
  (handled ÷ (window + drain)) for them.
- .NET's sample profiler on macOS samples at safepoints, which over-weights P/Invoke frames (GetTimestamp,
  GetSystemTimeAsTicks). Trust A/B numbers over profile percentages. 61% of GetTimestamp is the harness's own timer.

## 4. Final clean run (02:09–04:32, under caffeinate; all 27 invoke shapes, 4 lanes, 3 rounds rotating order, medians)

Lanes: 6.62 (net10), alpha.2, alpha.2 + lock-free ids (`7.0.0-perf.1`), alpha.2 + ids + cancellation shards
(`7.0.0-perf.5` = branch `perf/envelope-ids-and-cancellation-shards`, commits 8a2089e53 + 74acfad9a). Local-queue
shapes are quoted as handled/s. (An earlier attempt at this run without caffeinate was discarded: the Mac slept
at 01:11 and wrecked round 3.)

```
shape               6.62  alpha.2     +ids  +ids+cts   alpha.2    +ids  +ids+cts  (vs 6.62; handled/s for local-queue shapes; spread = max-min of 3 runs)
async              3890k    3718k    5259k     5401k     -4.4%  +35.2%   +38.8%          spread<=29%
cascade            2767k    2867k    2940k     3061k     +3.6%   +6.2%   +10.6%  handled spread<=23%
compound           2729k    2945k    2958k     2465k     +7.9%   +8.4%    -9.7%  handled spread<=8%
context            3250k    3729k    4992k     5392k    +14.7%  +53.6%   +65.9%          spread<=32%
deep-middleware    3213k    3687k    4989k     5417k    +14.7%  +55.2%   +68.6%          spread<=39%
fanout             1514k     758k    1775k     2568k    -49.9%  +17.2%   +69.6%  handled spread<=381%
graph              2952k    4158k    4994k     3277k    +40.8%  +69.2%   +11.0%  handled spread<=53%
headers            3168k    4013k    4931k     5006k    +26.7%  +55.6%   +58.0%          spread<=40%
middleware         3243k    3680k    4983k     5412k    +13.5%  +53.6%   +66.9%          spread<=33%
plain              3981k    3707k    5265k     5397k     -6.9%  +32.2%   +35.6%          spread<=22%
publish-local      1383k    1403k    1312k     2480k     +1.5%   -5.1%   +79.3%  handled spread<=43%
publish-stub       3395k    4668k    5773k     2458k    +37.5%  +70.0%   -27.6%          spread<=162%
queued             3237k    3686k    5195k     5406k    +13.9%  +60.5%   +67.0%          spread<=35%
remote-grpc         110k      79k      54k       79k    -28.5%  -50.9%   -28.1%          spread<=58%
remote-tcp            0k       0k       0k        0k    +11.1%  +38.0%   -39.8%          spread<=212%
reply              3843k    3486k    4976k     5047k     -9.3%  +29.5%   +31.3%          spread<=31%
retry                68k      66k      58k       60k     -2.5%  -13.8%   -11.5%          spread<=18%
saga               1183k    1169k    1162k     1155k     -1.2%   -1.8%    -2.4%          spread<=2%
schedule           2083k    2062k    2032k     2907k     -1.0%   -2.5%   +39.5%          spread<=17%
send-local         1619k    1470k    1771k     2525k     -9.2%   +9.4%   +56.0%  handled spread<=28%
send-stub          3249k    4404k    5910k     5644k    +35.6%  +81.9%   +73.7%          spread<=66%
sequential         3245k    4303k    5201k     5426k    +32.6%  +60.3%   +67.2%          spread<=40%
service            3182k    3908k    4019k     5403k    +22.8%  +26.3%   +69.8%          spread<=28%
stream             3080k    4255k    4879k     5059k    +38.2%  +58.4%   +64.2%          spread<=47%
tenant             3159k    4301k    4981k     5117k    +36.1%  +57.7%   +62.0%          spread<=36%
timeout            3241k    4511k    5284k     5404k    +39.2%  +63.0%   +66.7%          spread<=34%
valuetask          3929k    3678k    5263k     5398k     -6.4%  +33.9%   +37.4%          spread<=26%
faster than 6.62 on: alpha.2 17/27, +ids 22/27, +ids+cts 21/27
```

Reading it:
- **Lock-free envelope ids (8a2089e53) is the change to take.** Faster than 6.62 on 22 of 27 shapes, most by
  +30% to +80% (plain +32%, async +35%, middleware +54%, queued +61%, timeout +63%, graph +69%). Full CoreTests
  green (4,265 passed, 8 skipped).
- **Cancellation shards (74acfad9a) is a trade.** On top of the ids: fanout +70% → vs 6.62, publish-local +79%,
  send-local +56%, schedule +40%, service +70%; but graph falls from +69% to +11%, compound from +8% to −10%, and
  publish-stub is erratic (spread 162%). Probably moves the contention onto the local queue's channel lock, where
  .NET 11 is weakest.
- Still below 6.62 with the ids change: remote-grpc (.NET 11 runtime, −11% in the split run; this run noisy),
  retry −14% (60k ops/s, spread 18%), saga −2% (flat). remote-tcp is batch-timeout noise.
- Spreads were wider tonight (up to ~40% on in-process shapes) than in the 16:17 baseline (≤ 6–16%); the medians agree
  with the separate interleaved A/Bs above.
