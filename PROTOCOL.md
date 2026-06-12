# USB RF Power Meter V5 — Serial Protocol

Complete wire protocol of the Chinese "USB RF Power Meter V5" (STM32F401CCU6 +
AD8317/AD8318), as spoken by the **stock factory firmware**. As far as I can
tell this is the only public documentation of it.

**Method:** live byte captures against real hardware (2026-06-12), cross-checked
against an IL decompile of the vendor's `USB-RF-Power-Meter.exe` (.NET WinForms).
Everything below was verified on the wire unless marked otherwise. The capture
tool is in [`tools/capture.ps1`](tools/capture.ps1).

## Port settings

| Setting | Value |
|---|---|
| Baud | **460800** (the board uses a real USB-UART bridge, so baud matters — wrong baud = garbage) |
| Framing | 8N1, no flow control |
| DTR / RTS | leave low (vendor app never asserts them) |

The device **free-runs**: data streams the moment it has power. Opening the
port requires no handshake, no init command, nothing.

## The stream

### Record format (10 bytes)

```
[+|-] D D d D D D d d <unit>
 │    └┬┘ │ └─┬─┘ └┬┘  │
 │     │  │   │    │   └── watts unit AND record terminator:
 │     │  │   │    │       'u' = µW   'm' = mW   'w' = W
 │     │  │   │    └────── watts, fractional (.dd)
 │     │  │   └─────────── watts, integer (DDD)
 │     │  └─────────────── dBm, tenths (.d)
 │     └────────────────── dBm, integer (DD)
 └──────────────────────── sign of the dBm value

-58600000u  =  -58.6 dBm, 000.00 µW
+03600229m  =  +3.6 dBm, 002.29 mW
```

- The dBm value is **finished, calibrated power** — computed on the MCU using
  the band calibration selected by the frequency setting. Parse it; never
  recompute it from anything.
- Wire resolution is 0.1 dB.
- **Split the stream on all three unit chars** (`u`, `m`, `w`). A parser that
  only splits on `u` goes blind the moment the reading crosses 0 dBm (≥ 1 mW),
  which is exactly when you care.
- The watts field duplicates the dBm as DDD.dd in the unit given by the
  terminator. Below ~1 µW it reads 000.00, so recompute watts from dBm if you
  want resolution at low levels.

### Burst structure

Every **500 records** the device emits a two-byte block marker: `A` then `a`.

Each block is **one burst-sweep**: 500 consecutive samples acquired at the
configured sample period (see the K command), then transmitted in ~158 ms
(wire-limited at 460800 baud). Samples *within* a block are spaced by the real
sample period; *between* blocks there is a dead gap while the next sweep
happens. Sustained wire rate ≈ `500 / (500 × period + 158 ms)`.

This is how the vendor app draws µs-resolution envelope traces over a 46 kB/s
serial link: each block is a scope sweep, not a continuous feed. For
power-meter-style averaging the distinction washes out; for timing
measurements inside a block it's everything.

## Commands

All commands are CRLF-terminated (`\r\n`). **CR alone is silently ignored.**
Send each command as one clean write — see [Parser quirks](#parser-quirks).
The complete command set (confirmed by decompiling the vendor app — there is
nothing else):

### `Read\r\n` — query settings

The reply is injected into the stream **between the `A` and `a` of the next
block marker**:

```
R 5880 +00.0
  │    └──── external attenuation offset, signed, tenths of dB
  └───────── frequency in MHz, 4 digits, zero-padded
```

So at fast sample rates the reply shows up within ~160 ms; at the slowest
rates (multi-second sweeps) it can take a while. Don't busy-wait, just keep
parsing the stream.

### `A<freq:4><±##.#>\r\n` — set frequency and attenuation

Example: `A5880+10.0\r\n` → frequency 5880 MHz, attenuation +10.0 dB.

- **Frequency** (4 digits, MHz, zero-padded): selects the meter's **on-board
  band calibration**. The firmware derives its slope/intercept correction from
  this value, so set it to the band you're actually measuring.
- **Attenuation** (sign + two digits + `.` + one digit): the meter **adds this
  to the streamed values itself**. Verified: sending +10.0 shifts the stream by
  exactly +10 dB. A client must NOT add the offset again.
- Both fields are echoed verbatim by the next `Read`.

### ⚠️ The dangerous truncated form — never send it

`A` + 4 digits **without** the attenuation field (e.g. `A0010\r\n`) sets the
frequency but **corrupts the attenuation state**: the stream pegs at −99.9 dBm
and stays there.

**Recovery** (verified): send any full-form command, e.g. `A5880+00.0\r\n`.
The state is RAM-only, so a power cycle also clears it — but build your client
so it can't emit the short form in the first place. This repo's
`SerialWorker.SendFrequencyAndAttenuation` regex-validates the exact byte
shape before anything touches the port.

### `K01\r\n` … `K18\r\n` — set the burst sample rate

**Write-only.** The meter never reports this setting back; confirm it by
watching the record rate change. The stream pauses ~1 s while the device
reconfigures (true for the A command as well).

| K | sample period | burst rate | sweep length (500 samples) | sustained wire rate* |
|---|---|---|---|---|
| K01 | 2 µs | 500 kSa/s | 1 ms | ~3170 rec/s |
| K02 | 4 µs | 250 kSa/s | 2 ms | ~3170 rec/s |
| K03 | 8 µs | 125 kSa/s | 4 ms | ~3100 rec/s |
| K04 | 16 µs | 62.5 kSa/s | 8 ms | ~3030 rec/s |
| K05 | 32 µs | 31.3 kSa/s | 16 ms | ~2880 rec/s |
| K06 | 64 µs | 15.6 kSa/s | 32 ms | ~2630 rec/s |
| K07 | 128 µs | 7.8 kSa/s | 64 ms | ~2280 rec/s |
| K08, K09 | 256 µs | 3.9 kSa/s | 128 ms | ~1750 rec/s |
| K10, K11 | 512 µs | 2.0 kSa/s | 256 ms | ~1190 rec/s |
| K12 | 1024 µs | 1.0 kSa/s | 512 ms | ~730 rec/s |
| K13 | 2048 µs | 0.5 kSa/s | 1.0 s | ~340 rec/s |
| K14, K15 | 4096 µs | 244 Sa/s | 2.0 s | ~150–160 rec/s |
| K16, K17 | 8192 µs | 122 Sa/s | 4.1 s | ~160 rec/s |
| K18 | 16384 µs | 61 Sa/s | 8.2 s | ~60–160 rec/s |

\* measured live; the slow tail is approximate (capture windows shorter than
the sweep cycle). The duplicate pairs are real — the vendor app maps several
of its timebase positions to the same hardware rate. Sample periods come from
the decompiled vendor app's timebase tables and match the measured rates via
`500/(sweep + 158 ms)` to within a few percent.

Boot default is the K01/K02 region. The vendor app's red label (e.g.
"2ms - 250KSa/s") is `K02`: window = 500 × period, rate = 1/period.

## Parser quirks

The firmware's command parser is, to put it kindly, minimalist:

- **A stray byte before a command makes it unrecognized.** `\rRead\r\n` does
  nothing. Send commands as single atomic writes with no leading garbage.
- **The first `Read` after an `A` command is sometimes swallowed.** Retry once
  after ~1.5 s if no `R` reply appears. (This app does that automatically.)
- Commands chained in one write (e.g. `A...\rRead\r\n`) fail unpredictably.
  One command per write, give it a beat, then the next.

## Volatile settings

**Everything resets on power-cycle.** After a replug the meter reports
frequency `0001` (1 MHz!), attenuation `+00.0`, and the boot-default sample
rate. If your readings look ~2–10 dB off after replugging, you're on the wrong
band calibration — re-send your frequency.

## Hardware limits (so your detector survives)

- Absolute max input ≈ **+12 dBm**. Damage is instant.
- Useful linear range ≈ −50 … −5 dBm (AD8317); readings above ~−5 dBm compress.
- Detector noise floor on the tested unit: ≈ −58.6 dBm (at the 5880 MHz
  setting; shifts a little with the frequency/band calibration).
- The detector is broadband and untuned — it sums everything from ~1 MHz to
  8–10 GHz. Frequency setting changes the *calibration*, not the *selectivity*.

## Reference client

The C# protocol layer in
[`src/RfMeterGui/SerialWorker.cs`](src/RfMeterGui/SerialWorker.cs) is ~250
heavily-commented lines and implements all of the above: tokenizing on unit
chars, block markers, embedded R-replies, defensive command validation. Port
it to whatever language you like.
