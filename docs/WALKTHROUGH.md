# Walkthrough

A guided tour of the code, the reasoning behind each design decision, and interview questions with short
answers. File paths are relative to the repository root.

## 1. Reading order (about 30 minutes)

| # | File | What to look for |
|---|---|---|
| 1 | `src/TelemetryTriage.Core/Model/Telemetry.cs`, `Model/Incidents.cs` | `TelemetryFrame` (a missing channel means a dropout), `ChannelCatalog`, `AnomalyEvent` with deterministic ids, `Incident` |
| 2 | `src/TelemetryTriage.Core/Simulation/*` | Seeded generator; faults as data (`FaultSpec`) that the evals also use as ground truth |
| 3 | `src/TelemetryTriage.Core/Detection/ChannelDetector.cs` | Episode logic: one event when an anomaly starts, re-armed after 10 normal samples |
| 4 | `src/TelemetryTriage.Core/Detection/Detectors.cs` | The five detectors; each class comment states its blind spot |
| 5 | `src/TelemetryTriage.Core/Incidents/IncidentGrouper.cs` | Session window in event time |
| 6 | `src/TelemetryTriage.Core/Incidents/Evidence.cs` | `EvidencePack`: the only thing a triage provider sees |
| 7 | `src/TelemetryTriage.Core/Triage/RunbookIndex.cs` | Keyword retrieval with stable section ids |
| 8 | `src/TelemetryTriage.Core/Triage/CitationValidator.cs` | The guardrail |
| 9 | `src/TelemetryTriage.Core/Triage/RuleBasedTriage.cs`, `TriageService.cs` | Baseline provider; validate-or-fallback |
| 10 | `src/TelemetryTriage.Core/Pipeline/TriagePipeline.cs` | Dedup, detect, group, bounded triage queue, ack |
| 11 | `src/TelemetryTriage.Infrastructure/Kafka/KafkaTelemetryBus.cs` | Offset handling for at-least-once delivery |
| 12 | `src/TelemetryTriage.Infrastructure/Claude/ClaudeTriageProvider.cs` | SDK call, stop-reason handling, typed errors, cost |
| 13 | `src/TelemetryTriage.Core/Observability/TriageTelemetry.cs` | Every metric and span in one place |
| 14 | `src/TelemetryTriage.Core/Evals/EvalRunner.cs`, `evals/golden-set.json` | Property-based evals |
| 15 | `src/TelemetryTriage.Api/*`, `src/TelemetryTriage.Cli/Program.cs` | Thin hosts over one composition root (`Infrastructure/ServiceCollectionExtensions.cs`) |

## 2. The life of one frame

1. `TelemetryGenerator` yields a `TelemetryFrame` (sequence 305, six readings).
2. `ITelemetryBus.PublishAsync` writes it to a bounded `Channel<T>` (in-memory) or to Kafka.
3. `TriagePipeline` reads a `BusMessage`. If `Sequence <= highWaterMark`, the frame is a redelivery: it is
   acked and dropped.
4. The frame goes into `FrameHistory`, a ring buffer used later to cut evidence windows.
5. Each detector runs on it. `ChannelDetector` turns per-sample verdicts into episode starts, so at sample 305
   `link_snr` gets one `zscore` event, not one event per bad sample.
6. `IncidentGrouper.Add` appends the anomaly to the stream's open incident. `CloseExpired(frame.Timestamp)`
   closes incidents whose latest anomaly is more than 10 s old in **event time**.
7. For each closed incident, `EvidenceBuilder` builds the evidence pack: channel windows, a pre-incident
   baseline, and the top 3 runbook sections. The pack goes onto the bounded triage queue.
8. The frame is acked: in Kafka, its offset is stored and committed in the background.
9. A triage worker calls `TriageService`: provider, then `CitationValidator`, then accept or fall back to the
   rule-based report. The result is upserted into `IIncidentStore` and served by `/incidents`.

## 3. Design decisions and why

### Deterministic detectors first, LLM second

- **Latency and cost:** detection runs on every frame (10 Hz per stream; typically 25-50 microseconds for all
  five detectors, measured), so it must be cheap. LLM calls run once per closed incident, which is orders of magnitude rarer.
- **Explainability and testability:** a z-score of -19.0 against a stated mean and std is auditable. Detector
  behaviour is unit-tested and its false-alarm rate is measured (2 episodes in 300 nominal minutes).
- **Failure isolation:** if the LLM is down, rate-limited, or refuses, detection and incident grouping still
  work, and the operator still gets the rule-based triage. The LLM adds narrative and cross-channel reasoning;
  it never sits on the detection path.
- **The LLM's real job:** connecting evidence across channels and runbooks ("bus sag explains the SNR drop") and
  writing it up for a human, which is hard to encode in rules for every combination.

### Why two statistical detectors

A rolling z-score compares each sample to the last 100 *normal* samples. It is good for sudden changes, but a
slow ramp drags the window along with it, so the z-score never grows. The test
`Rolling_zscore_misses_slow_drift_but_ewma_catches_it` demonstrates this. `EwmaDriftDetector` compares a smoothed
average against a baseline **frozen after warm-up**, so drift accumulates. Two details matter:

- Inputs are clipped to the baseline +/- 4 sigma (winsorized), so one spike cannot look like drift
  (`Ewma_ignores_a_single_spike_because_inputs_are_clipped`).
- The control limit includes the standard error of the baseline mean: `sqrt(alpha/(2-alpha) + 1/warmup)`.
  Without that term, nominal runs produced false alarms. That was found by measuring over many seeds, not
  assumed.

The rate-of-change detector encodes physics ("28 V cannot become 27 V in 0.1 s"). Stuck-value and missing-data
detect instrumentation faults, which mean *loss of observability* rather than a physical excursion. The triage
treats that difference explicitly.

### Episodes, not samples

Emitting an event for every anomalous sample would flood the grouper and the LLM prompt with near-duplicates.
`ChannelDetector` emits once when an episode starts and re-arms after 10 normal samples. The trade-off: a
persistent fault does not re-alert. That is listed as a limitation.

### Event-time session windows

Grouping uses frame timestamps, not the wall clock. Replays, backfills, and live data therefore group
identically, and tests are deterministic. A session window (close after a 10 s gap) fits bursts of related
anomalies better than fixed windows, which can split one incident across a boundary.

### At-least-once vs exactly-once

- The Kafka consumer sets `EnableAutoOffsetStore=false` and `EnableAutoCommit=true`. The pipeline calls
  `StoreOffset` only after a frame is processed (`BusMessage.AckAsync`), and the background commit sends only
  stored offsets. A crash means unacked frames are delivered again, never skipped. The integration test
  `Unacknowledged_frames_are_redelivered_to_the_same_consumer_group` proves this against a real broker.
- Redelivery is made harmless rather than prevented. (1) Frames carry a sequence number, and the pipeline drops
  anything at or below its high-water mark. That is valid because one partition preserves order. (2) Incident
  ids are deterministic, so reprocessing upserts the same incident.
- Kafka's exactly-once (transactions) only covers Kafka-to-Kafka. Here the side effects are an LLM call and a
  store write, so a transaction cannot cover them. Idempotent consumers are the standard answer.
- The honest gap: with the in-memory store, the frame is acked once the incident is queued, not stored. In
  production you would write the closed incident to a durable outbox before acking, and run triage from the
  outbox.

### Backpressure with bounded channels

There are two queues, and both are bounded with `FullMode.Wait`:

1. **In-memory bus (1024 frames):** if detection falls behind, `PublishAsync` waits instead of growing memory.
   `Bounded_bus_applies_backpressure_when_full` tests this.
2. **Triage queue (16 incidents, 2 workers):** a slow LLM fills the queue. When it is full, ingestion waits,
   which slows Kafka consumption, so lag grows *in the broker*, where it is durable and visible as consumer lag.
   The alternative, an unbounded queue, turns a slow dependency into an out-of-memory crash.

`TriageConcurrency` also caps the LLM spend rate and rate-limit pressure.

### Citation validation

The LLM receives an evidence pack where every item has an id (`anom:...`, `win:...`, `rb:...`). The validator
then checks the report against that same serialized pack:

- every cited id exists, at least one anomaly is cited, and every recommended check cites something;
- affected subsystems appear in the evidence;
- every number in the free text appears somewhere in the evidence, at the precision written (`24.8` is
  grounded by `24.812`, but `24.9` is not). Numbers inside identifiers are ignored. Integers up to 10 are exempt
  as counts.

A rejected report is replaced by the rule-based one, recorded as `RejectedUsedFallback` with the reasons, and
counted in `triage.validation.failures`. What the validator *cannot* catch: a real number attributed to the
wrong channel, or reasoning that is wrong but well cited. Evals and human review cover those.

The schema is static rather than a per-request `enum` of valid ids, because a static schema is compiled once
and cached, while a per-incident schema pays compilation latency on every call. That is a deliberate trade-off
and is on the roadmap to measure.

### Cost and latency tracking

- `triage.duration` covers the provider call, validation, and fallback, because that is what the operator
  actually waits for.
- Tokens are recorded with OpenTelemetry GenAI semantic-convention names, so standard dashboards understand
  them. Cost is computed from the *served* model (which could be a fallback model) and a configurable price
  table.
- Histograms use explicit bucket boundaries through the BCL `InstrumentAdvice` API. The default buckets
  (0-5 ms in a single bucket) hid the fact that detection typically takes 25-50 microseconds.
- No span per frame: at 10 Hz per stream that would be noise and storage cost. Per-frame work goes to
  histograms; spans are kept for the pipeline run and each triage.
- Effort is the main cost and latency knob on this model (thinking is always adaptive). It is configured
  explicitly, and the evals are the place to compare `low` against `medium`.

### Claude-specific handling

- `stop_reason` is checked **before** content is read. `refusal` and `max_tokens` both mean the JSON cannot be
  trusted.
- Server-side fallback (`fallbacks: "default"`) lets a policy refusal be re-served by another model in the same
  call. If the whole chain still refuses, the rule-based report is used.
- There is no prefill and no forced `tool_choice`: structured outputs provide the JSON guarantee.
- Typed exceptions separate retryable failures (429, 5xx, network; the SDK retries them) from non-retryable
  ones (400: a bug in the request).

### Testing strategy

- **Fake `HttpMessageHandler`** under the real `AnthropicClient`: this tests serialization (fallbacks, beta
  header, schema, effort) and deserialization (stop reasons, usage) without a key. The fixtures are labeled as
  hand-written.
- **`WebApplicationFactory<Program>`** boots the real API, including the background pipeline.
- **`[KafkaFact]`** runs the integration tests only when `KAFKA_BOOTSTRAP_SERVERS` is set, so they show as
  *skipped*, not as falsely passing.
- **Evals as tests, plus a negative control:** a deliberately hallucinating provider must make the evals fail.

## 4. Likely interview questions

**Why not let the LLM detect anomalies directly from the stream?**
It is too slow and expensive for 10 Hz per channel, it is non-deterministic, and it is hard to bound its false
alarms. Statistical detectors are microseconds per frame, testable, and explainable. The LLM is used where it
adds value: correlating evidence and writing the triage.

**How do you know the LLM is not making things up?**
Every claim must cite an id from the evidence pack. A validator rejects unknown ids, unknown subsystems, and
numbers that do not appear in the evidence, and rejected reports fall back to the deterministic triage. The
evals include a negative control that proves the checks catch hallucinations. Remaining risk: correct numbers
used in a wrong argument. That needs evals with graded references and human review.

**What delivery guarantee do you provide?**
At-least-once: offsets are stored only after processing. Duplicates are neutralized by a sequence high-water
mark and deterministic incident ids. Exactly-once does not extend to an LLM call or a database write, so
idempotency is the practical choice.

**What happens if Claude is slow or down?**
Triage runs off the hot path behind a bounded queue with 2 workers. Slowness backs up into Kafka consumer lag,
and detection keeps working. Errors, refusals, and truncation all produce the rule-based triage with the reason
recorded. Health stays green, because the pipeline itself is fine.

**How would you scale to many vehicles or ground stations?**
Use one topic (or key) per stream and a consumer group across instances. Kafka assigns partitions, and each
partition is processed in order by one consumer. Detector state is per stream, so it shards naturally. Move the
store to SQL Server, add an outbox before the offset commit, and autoscale on consumer lag.

**How did you choose the thresholds?**
From the noise model: z > 5 with a noise floor, an EWMA limit from control-chart theory plus the baseline
uncertainty, and rate limits at about 6x the per-sample rate noise. Then I measured the false-alarm rate on 300
nominal runs and wrote a test that bounds it. With real data you would fit per-mode baselines and tune against
labeled incidents.

**Why keyword retrieval instead of embeddings?**
With 6 runbooks, keyword overlap is transparent, deterministic, and debuggable. When it ranked an irrelevant
section, I could see exactly why: plural forms and a generic term. Embeddings or BM25 make sense once the corpus
is large enough that vocabulary mismatch dominates.

**How do you track LLM cost?**
Tokens per call are recorded with OpenTelemetry GenAI conventions, and cost is estimated from the served model's
price in config. Both go on the triage span and on histograms, so you can see cost per incident, per model, and
per day. Effort level and concurrency are the control knobs.

**What would you do next to make this production-grade?**
A durable store and outbox, a dead-letter topic and schema registry, detector state snapshots, live evals that
compare Claude with the baseline on quality, latency, and cost, a repair loop for rejected reports, and
dashboards and alerts on consumer lag, validation failure rate, and cost.

**What is the weakest part of the design?**
Number grounding checks presence, not attribution, and incident close relies on a fixed 10 s gap. Both are
listed as limitations, along with what I would do about them.
