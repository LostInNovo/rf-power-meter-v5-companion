# How it works — A to Z

This is the full tour of the RF Power Meter V5 Companion: what every part does, how
the data flows from the meter's serial bytes up to the number on screen, and where
each feature lives in the code. If you want the raw wire protocol instead, that's in
[PROTOCOL.md](../PROTOCOL.md).

---

## 1. The 10,000-foot view

The meter is dumb-simple over USB: it free-runs and **streams finished dBm values** as
ASCII over a serial port at 460800 baud. The app's whole job is:

```
  ┌──────────┐   USB serial    ┌─────────────────┐   sample bin    ┌──────────────┐
  │  Meter   │  ───────────▶   │  SerialWorker   │  ───────────▶   │  MainWindow  │
  │ (STM32 + │   460800 8N1    │  (bg thread)    │   (locked)      │  (UI thread, │
  │  AD8317) │   dBm records   │  parses bytes   │   avg/min/max   │   50 ms tick)│
  └──────────┘                 └─────────────────┘                 └──────┬───────┘
                                                                          │ fans out to
                         ┌────────────────┬───────────────┬──────────────┼───────────────┐
                         ▼                ▼               ▼              ▼               ▼
                     readout +        statistics     floor tare      trigger        SignalChart
                     watts + bar      min/max/avg     Δ / net         capture        (time series)
                                                                                     + CSV log
```

Two threads, cleanly separated:

- **Background serial thread** ([`SerialWorker`](../src/RfMeterGui/SerialWorker.cs)) does nothing but read bytes and parse them into dBm samples, which it accumulates into a small locked "bin."
- **UI thread** ([`MainWindow`](../src/RfMeterGui/MainWindow.xaml.cs)) wakes up every 50 ms, drains that bin, and updates everything on screen.

The serial thread never touches UI; the UI thread never blocks on serial. That's the
entire concurrency story.

---

## 2. The serial layer — `SerialWorker.cs`

### What the meter sends

A continuous stream of 10-byte records, e.g. `-58600000u`:

```
  [+|-] D D d D D D d d <unit>
   │    └┬┘ │ └─┬─┘ └┬┘  │
   sign  │  │   │    │   └─ watts unit AND record terminator: 'u'=µW 'm'=mW 'w'=W
   of    │  │   │    └───── watts, fractional (.dd)
   dBm   │  │   └────────── watts, integer (DDD)
         │  └────────────── dBm, tenths (.d)
         └───────────────── dBm, integer (DD)
```

So `-58600000u` = **−58.6 dBm**, 000.00 µW. The dBm is already calibrated on the MCU —
the app **parses** it, it never recomputes power from a voltage.

### How it's parsed

`ReadStreamLoop()` runs on the background thread and reads raw bytes in 8 KB chunks. It
splits the stream into **tokens** at the unit character (`u`/`m`/`w`) — that terminator
is the record delimiter:

```csharp
if (c is 'u' or 'm' or 'w') ParseCompletedToken();
else _tokenBuffer.Append(c);
```

> ⚠️ Splitting on all three unit chars matters: a parser that only splits on `u` goes
> blind the moment a reading crosses 0 dBm (≥ 1 mW), which is exactly when you care.

`ParseCompletedToken()` takes the last 9 chars of a token (sign + 8 digits), validates
they're digits, and computes dBm from the first three (`±DD.d`) with a manual digit math
loop — no `Substring`/`double.Parse` allocations, because this runs ~3,000×/second. The
watts field is ignored (the UI recomputes watts from dBm so it keeps resolution at low
levels where the meter prints `000.00`).

Every parsed sample is folded into the **bin** under a lock:

```csharp
_binCount++; _binSum += dbm;
if (dbm < _binMin) _binMin = dbm;
if (dbm > _binMax) _binMax = dbm;
_binLast = dbm;
```

### The bin handoff

The UI calls `DrainBin()` every tick. It atomically snapshots the bin into a
`BinSnapshot` (count, sum, min, max, last) and resets it. `BinSnapshot.Avg` is just
`Sum / Count`. This is the only shared state between the two threads, and it's tiny and
lock-guarded.

### Commands the app can send

Three, all CRLF-terminated, written as single clean writes (the meter's parser desyncs
on stray leading bytes):

| Method | Wire bytes | Purpose |
|---|---|---|
| `RequestMeterSettings()` | `Read\r\n` | ask for current freq + attenuation |
| `SendFrequencyAndAttenuation(mhz, db)` | `A5880+10.0\r\n` | set band-cal frequency + offset |
| `SendSampleRate(k)` | `K08\r\n` | set the burst sample rate (write-only) |

`SendFrequencyAndAttenuation` regex-validates the exact byte shape before sending —
the truncated `A####` form (no attenuation field) is known to corrupt the meter's state,
and this makes it impossible to emit by accident.

The `Read` reply (`R5880+00.0`) arrives **embedded in the stream** between the `A` and `a`
of the next block marker, so `ParseCompletedToken` also watches for an `R...` and raises
`MeterSettingsReceived(freq, atten)`. If the serial thread dies, it raises
`ConnectionFailed(msg)`. Both events are marshalled to the UI thread by `MainWindow`.

---

## 3. The heartbeat — `OnUiTimerTick` (every 50 ms)

This is the spine of the app. Each tick:

1. `var bin = _meter.DrainBin();` — grab everything since last tick.
2. If the bin has samples:
   - append a `ChartSample(now, avg, min, max)` to the rolling history;
   - update the **big dBm number**, the **watts** readout, the **signal bar**;
   - update **peak hold** if this bin's max beat the record;
   - fold into **running statistics** (min/max/avg since reset);
   - write a **CSV row** if logging;
   - update the **floor tare** readout;
   - run the **trigger** check.
3. If the stream has stalled > 2 s (e.g. the meter is reconfiguring after a command),
   show `--.-` / "no data".
4. Once a second, recompute and show the **records/sec** rate in the status line.
5. Hand the chart its data: `TimeSeriesChart.Update(...)`.

Everything below is one of those fan-out steps.

---

## 4. Features, explained

### 4.1 Live readout (dBm → watts)

The dBm number is just `bin.Avg` formatted. Watts come from:

```csharp
DbmToWatts(dbm) = 10^((dbm − 30) / 10)        // dBm → watts
```

and `FormatWatts` auto-scales the unit (W / mW / µW / nW / pW) so you always read a sane
number. The signal bar is a `ProgressBar` clamped to −80…+10 dBm.

### 4.2 Peak hold

A single `_peakHoldDbm` that only ever goes up (until you press **Reset peak**). It's
also drawn on the chart as a dashed line, colored by its own strength.

### 4.3 Statistics (since reset)

Running `min`, `max`, and a `sum/count` average accumulated across every bin since the
last **Reset**. Separate from peak hold so you can reset them independently.

### 4.4 Noise-floor tare — the "honest signal" feature

RF coupling near the noise floor is deceptive: a reading 2 dB above the floor is still
~60 % floor *by power*. The tare does the correct subtraction for you.

- **Capture floor**: averages the current readings into a reference `_floorDbm`.
  - **Click** = average 1 second.
  - **Press and hold** = average for as long as you hold (longer = tighter reference;
    at 3,000 rec/s a 10 s hold averages ~30,000 samples).
- Once captured, the readout shows two things live:
  - **Δ above floor** (your relative "zero"), and
  - **net dBm** = the floor-corrected true contribution, computed as
    `WattsToDbm(DbmToWatts(reading) − DbmToWatts(floor))`.
- Below ~0.2 dB of Δ the subtraction is all jitter, so it just says "at/below floor."

The click-vs-hold logic lives across `FloorCaptureButton_MouseDown` (starts an
open-ended capture), `_MouseUp` (a >1 s hold finalizes immediately; a quick click lets it
run to the 1 s mark), and `_Click` (keyboard activation falls back to the 1 s capture).

### 4.5 Threshold trigger capture

Catches transmitter power-ups while you're not watching. When **enabled** and the level
stays **above the threshold for two consecutive bins (~100 ms)**, it timestamps one entry
into the list, then goes quiet until the level drops 2 dB below the threshold and
**re-arms**.

> The two-bin debounce is deliberate: the meter's per-bin noise spread is 1–1.5 dB even
> on a dead floor, so a single-bin spike would false-fire. (An earlier version required
> the bin spread to be < 0.3 dB to "settle" — that condition could never pass on real
> hardware, so the trigger never fired. The debounce replaced it.)

### 4.6 CSV logging

Toggle **Start/Stop**. Writes `Documents\rfmeter\logs\rf_<timestamp>.csv` with one row
per 50 ms bin: `timestamp,avg_dbm,min_dbm,max_dbm,samples`. Buffered, flushed every 20
rows and on stop/close.

### 4.7 Meter settings (stored on the device)

The meter holds three settings; the app reads and writes them:

- **Frequency (MHz)** — selects the meter's on-board *band calibration*. It does **not**
  make the broadband detector frequency-selective (that's why there's no spectrum
  analyzer — see PROTOCOL.md). Set it to the band you're measuring.
- **Attenuation (dB)** — an offset the meter adds to the stream *itself*. The app
  therefore never adds it a second time. Enter your external pad value here so the
  reading shows true power.
- **Sample rate (K01–K18)** — the burst sample period, 500 kSa/s down to 61 Sa/s.
  Write-only: the meter never reports it back, so the dropdown is confirmed by watching
  the rec/s figure change.

**Send to meter** validates the fields, sends `A<freq><±atten>`, then polls `Read` back
~0.5 s later to **verify the echo** matches what was sent (and retries once, because the
meter sometimes swallows the first poll after an `A`). The controls stay locked until the
first successful read, so you always see what the meter actually has before changing it.

> Settings are **RAM-only** — a power cycle resets the meter to 1 MHz / +0.0 dB / boot
> sample rate. The "Meter reports:" line always shows the live truth, so a wrong band is
> obvious on connect. Re-send your frequency after replugging.

---

## 5. The chart — `SignalChart.cs`

A custom `FrameworkElement` that draws the whole time-series in **one `OnRender` pass** —
no per-frame WPF element churn. Key ideas:

- **Decimation**: the visible window is divided into ~2-pixel columns; all bins falling
  in a column collapse to one avg + one max. So render cost is bounded by the chart's
  **width**, not by how much history has accumulated. (The old Canvas-based chart added a
  `Line`/`Polyline` element per point and got slower the longer it ran — this fixes that.)
- **Value coloring**: traces are stroked with a vertical gradient brush whose colors line
  up with the dBm axis (violet floor → blue → amber → orange → red hot). So a trace is
  automatically colored by its level, Grafana-style. `ColorForDbm(dbm)` interpolates that
  scale; it's also reused anywhere else that needs a strength color.
- **Layers**, back to front: dBm + time gridlines and labels → translucent **area fill**
  under the average trace → thin **max-hold envelope** → solid **average trace** → dashed
  **peak-hold line**.
- **Auto-ranging**: the Y axis snaps to 5 dB steps around the visible data, clamped to a
  sane −110…+30 dBm.

`Update(bins, now, windowSeconds, showMax, peakHoldDbm)` is the only entry point; it
stores the references and calls `InvalidateVisual()`. The window selector (10 s / 30 s /
60 s / 5 min) and the **Max trace** checkbox feed straight into it.

---

## 6. The look — `App.xaml` + the window

- **`App.xaml`** is a single themed resource dictionary (the "Nebula" purple theme): the
  color palette, gradient brushes, and full control templates for buttons, the accent
  button, caption buttons, text boxes, combo boxes, check boxes, the progress bar, list
  box, slim scrollbars, tabs, and tooltips. Restyling the whole app is one file.
- The window is **borderless** (`WindowStyle="None"` + `WindowChrome`) with a **custom
  title bar**: the RF-wave glyph, the title, and min/maximize/close buttons wired to
  `MinimizeButton_Click` / `MaximizeButton_Click` / `CloseButton_Click`. Dragging and
  edge-resize still work via `WindowChrome`.
- The **connection status dot** (an `Ellipse`) is recolored from code: grey idle, violet
  connected, red on error.
- The big dBm readout has a slow "breathing" glow driven by a XAML storyboard.

### Small shared helpers in `MainWindow.xaml.cs`

- `TryParseUserNumber` — parses a typed number accepting either `.` or `,` as the decimal
  separator (used by the attenuation and trigger-threshold fields).
- `DbmToWatts` / `WattsToDbm` / `FormatWatts` — the power math and unit formatting.
- `PopulateSampleRateList` — builds the K01–K18 dropdown labels from the per-K sample
  periods (from the decompiled vendor app) and the live-measured wire rates.

---

## 7. File map

| File | Role |
|---|---|
| `src/RfMeterGui/SerialWorker.cs` | Serial port + background parser; the protocol layer |
| `src/RfMeterGui/MainWindow.xaml` | Window layout (title bar, left panel, chart) |
| `src/RfMeterGui/MainWindow.xaml.cs` | The UI controller and 50 ms tick loop |
| `src/RfMeterGui/SignalChart.cs` | The time-series chart (custom `OnRender`) |
| `src/RfMeterGui/App.xaml` | The whole visual theme + control templates |
| `src/RfMeterGui/RfMeterGui.csproj` | net7.0-windows, icon, `System.IO.Ports` dep |
| `app.ico` | The app/taskbar icon |
| `PROTOCOL.md` | The reverse-engineered wire protocol (the moat) |

---

## 8. Build, run, flags

```
dotnet build src/RfMeterGui/RfMeterGui.csproj -c Release
```

Self-contained single-file release build (what the GitHub releases ship):

```
dotnet publish src/RfMeterGui/RfMeterGui.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

CLI flags (handy for testing): `--autoconnect`, `--log` (start CSV immediately),
`--size=WxH` (open at a given size).

---

## 9. A note on safety

The AD8317/8318 input dies instantly above ~+12 dBm. Never wire a transmitter straight
in — use attenuators and enter the pad value in the attenuation field. Full guidance is
in the [README](../README.md#how-not-to-fry-it-read-this-once-seriously).
