using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace RfMeterGui;

/// <summary>
/// All dBm samples accumulated between two <see cref="SerialWorker.DrainBin"/> calls,
/// reduced to the values the UI needs (count/sum for averaging, min/max envelope, last value).
/// </summary>
public readonly record struct BinSnapshot(int Count, double Sum, double Min, double Max, double Last)
{
    /// <summary>Mean dBm of the bin, or NaN when the bin is empty.</summary>
    public double Avg => Count > 0 ? Sum / Count : double.NaN;
}

/// <summary>
/// Owns the serial port and a background thread that parses the RF Power Meter V5's
/// free-running stream into dBm samples and meter-settings responses.
///
/// Wire protocol (verified live on COM10 @ 460800 8N1 on 2026-06-12 and cross-checked
/// against the decompiled vendor application — see Documents\rfmeter\README.md):
///
///   Stream record (10 bytes):  [+|-] DD d DDD dd  + unit char
///       chars 1-3  = power as ±DD.d dBm
///       chars 4-8  = power as DDD.dd watts, scaled by the terminator:
///                    'u' = microwatt, 'm' = milliwatt, 'w' = watt
///     The stream is bursty: every "Aa" marker delimits one 500-sample sweep acquired
///     at the configured sample period (2 us .. 16.4 ms), shipped in ~158 ms.
///
///   Commands (CRLF-terminated, single clean writes — the firmware's parser is
///   easily desynced by stray bytes):
///       "Read\r\n"             request settings; the reply "R&lt;freq:4&gt;&lt;±##.#&gt;" arrives
///                              embedded between the 'A' and 'a' of the next block marker
///       "A&lt;freq:4&gt;&lt;±##.#&gt;\r\n" set frequency (MHz, selects the on-board band
///                              calibration) and attenuation offset (dB, applied to the
///                              streamed values by the meter itself)
///       "K01".."K18" + "\r\n"  set the burst sample rate (write-only)
///
///   DANGER: an A command without the attenuation field (e.g. "A0010\r\n") corrupts the
///   meter state until a full-form command is sent. <see cref="SendFrequencyAndAttenuation"/>
///   refuses to emit anything but the verified full form.
/// </summary>
public sealed class SerialWorker : IDisposable
{
    /// <summary>Matches the meter-settings reply: "R" + 4-digit frequency + signed attenuation.</summary>
    private static readonly Regex MeterSettingsRegex = new(@"R(\d{4})([+-]\d\d\.\d)", RegexOptions.Compiled);

    /// <summary>The only A-command shape that is safe to send (see class remarks).</summary>
    private static readonly Regex SafeSetCommandRegex = new(@"^A\d{4}[+-]\d\d\.\d\r\n$", RegexOptions.Compiled);

    private SerialPort? _port;
    private Thread? _readThread;
    private volatile bool _running;

    // One "bin" of samples, guarded by _binLock; the UI thread drains it periodically.
    private readonly object _binLock = new();
    private int _binCount;
    private double _binSum, _binLast;
    private double _binMin = double.PositiveInfinity;
    private double _binMax = double.NegativeInfinity;

    private long _totalRecords;
    private long _malformedTokens;

    /// <summary>Bytes of the token currently being assembled (everything since the last unit char).</summary>
    private readonly StringBuilder _tokenBuffer = new(64);

    /// <summary>
    /// Raised (on the worker thread) when a settings reply arrives, with the raw fields:
    /// frequency in MHz (e.g. "5880") and attenuation in dB (e.g. "+00.0").
    /// </summary>
    public event Action<string, string>? MeterSettingsReceived;

    /// <summary>Raised (on the worker thread) when the read loop dies unexpectedly.</summary>
    public event Action<string>? ConnectionFailed;

    /// <summary>True while the serial port is open.</summary>
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>Total stream records parsed since <see cref="Connect"/> (drives the rec/s display).</summary>
    public long TotalRecords => Interlocked.Read(ref _totalRecords);

    /// <summary>Tokens that did not end in a valid record. One partial token at connect is normal.</summary>
    public long MalformedTokens => Interlocked.Read(ref _malformedTokens);

    /// <summary>Open the port (8N1, no handshake, DTR/RTS low — matching the meter) and start parsing.</summary>
    public void Connect(string portName, int baud)
    {
        Disconnect();
        var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 500,
            WriteTimeout = 1000,
        };
        try
        {
            port.Open();
            port.DiscardInBuffer();
        }
        catch
        {
            port.Dispose();
            throw;
        }
        _port = port;
        _running = true;
        Interlocked.Exchange(ref _totalRecords, 0);
        Interlocked.Exchange(ref _malformedTokens, 0);
        _tokenBuffer.Clear();
        _readThread = new Thread(ReadStreamLoop) { IsBackground = true, Name = "rfmeter-serial" };
        _readThread.Start();
    }

    /// <summary>Stop the read thread and close the port. Safe to call when already disconnected.</summary>
    public void Disconnect()
    {
        _running = false;
        var port = _port;
        _port = null;
        if (port != null)
        {
            try { port.Close(); } catch { /* already gone */ }
            port.Dispose();
        }
        _readThread?.Join(1500);
        _readThread = null;
    }

    /// <summary>
    /// Ask the meter for its current frequency/attenuation. The reply arrives asynchronously
    /// via <see cref="MeterSettingsReceived"/> within ~1 block (158 ms at fast sample rates).
    /// </summary>
    public void RequestMeterSettings() => WriteCommand("Read\r\n");

    /// <summary>
    /// Set the burst sample rate, index 1..18 ("K01".."K18": 500 kSa/s down to 61 Sa/s).
    /// Write-only — the meter never reports it back; confirm via the observed record rate.
    /// The stream pauses ~1 s while the meter reconfigures.
    /// </summary>
    public void SendSampleRate(int kIndex)
    {
        if (kIndex is < 1 or > 18)
            throw new ArgumentOutOfRangeException(nameof(kIndex), "sample-rate index must be 1..18");
        WriteCommand($"K{kIndex:00}\r\n");
    }

    /// <summary>
    /// Write the measurement frequency (MHz — selects the meter's on-board band calibration)
    /// and the external attenuation offset (dB — added to the streamed values by the meter).
    /// Both fields are mandatory: the short A-command form corrupts the meter state.
    /// </summary>
    public void SendFrequencyAndAttenuation(int frequencyMhz, double attenuationDb)
    {
        if (frequencyMhz is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(frequencyMhz), "frequency must be 1..9999 MHz");
        if (double.IsNaN(attenuationDb) || Math.Abs(attenuationDb) > 99.9)
            throw new ArgumentOutOfRangeException(nameof(attenuationDb));
        var sign = attenuationDb < 0 ? '-' : '+';
        var command = $"A{frequencyMhz:0000}{sign}{Math.Abs(attenuationDb).ToString("00.0", System.Globalization.CultureInfo.InvariantCulture)}\r\n";
        if (!SafeSetCommandRegex.IsMatch(command))
            throw new InvalidOperationException($"refusing to send malformed command: {command}");
        WriteCommand(command);
    }

    /// <summary>Send one command as a single atomic write (the meter's parser cannot resync mid-command).</summary>
    private void WriteCommand(string command)
    {
        var port = _port ?? throw new InvalidOperationException("not connected");
        var bytes = Encoding.ASCII.GetBytes(command);
        port.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Return everything accumulated since the previous call and start a fresh bin.</summary>
    public BinSnapshot DrainBin()
    {
        lock (_binLock)
        {
            var snapshot = new BinSnapshot(_binCount, _binSum, _binMin, _binMax, _binLast);
            _binCount = 0;
            _binSum = 0;
            _binMin = double.PositiveInfinity;
            _binMax = double.NegativeInfinity;
            return snapshot;
        }
    }

    /// <summary>
    /// Worker-thread body: read raw bytes and split them into tokens at the record
    /// terminator (the watts unit char 'u'/'m'/'w'). Exits when <see cref="Disconnect"/>
    /// closes the port.
    /// </summary>
    private void ReadStreamLoop()
    {
        var buffer = new byte[8192];
        while (_running)
        {
            int bytesRead;
            try
            {
                var port = _port;
                if (port == null) break;
                bytesRead = port.BaseStream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (_running) ConnectionFailed?.Invoke(ex.Message);
                return;
            }
            for (int i = 0; i < bytesRead; i++)
            {
                char c = (char)buffer[i];
                if (c is 'u' or 'm' or 'w') ParseCompletedToken();
                else
                {
                    if (_tokenBuffer.Length >= 64) _tokenBuffer.Remove(0, 32); // runaway garbage; keep the tail
                    _tokenBuffer.Append(c);
                }
            }
        }
    }

    /// <summary>
    /// Handle one completed token (the bytes between two unit chars). A token normally ends
    /// in a 9-char record (sign + 8 digits) and may additionally carry "Aa" block-marker
    /// chars and/or an embedded settings reply in front of it.
    /// </summary>
    private void ParseCompletedToken()
    {
        var token = _tokenBuffer.ToString();
        _tokenBuffer.Clear();

        // Settings reply, e.g. "AR5880+00.0Aa-58600000" — extract it wherever it sits.
        if (token.Length > 9 && token.Contains('R'))
        {
            var match = MeterSettingsRegex.Match(token);
            if (match.Success) MeterSettingsReceived?.Invoke(match.Groups[1].Value, match.Groups[2].Value);
        }

        // The record is the last 9 chars: sign + 3 dBm digits (±DD.d) + 5 watts digits.
        // The watts field is ignored — the UI recomputes watts from dBm, which preserves
        // resolution at low levels where the meter prints 000.00.
        if (token.Length >= 9)
        {
            int start = token.Length - 9;
            char sign = token[start];
            if (sign is '+' or '-')
            {
                bool allDigits = true;
                for (int i = 1; i <= 8 && allDigits; i++)
                    if (token[start + i] is < '0' or > '9') allDigits = false;
                if (allDigits)
                {
                    double dbm = ((token[start + 1] - '0') * 10 + (token[start + 2] - '0') + (token[start + 3] - '0') * 0.1)
                                 * (sign == '-' ? -1 : 1);
                    lock (_binLock)
                    {
                        _binCount++;
                        _binSum += dbm;
                        if (dbm < _binMin) _binMin = dbm;
                        if (dbm > _binMax) _binMax = dbm;
                        _binLast = dbm;
                    }
                    Interlocked.Increment(ref _totalRecords);
                    return;
                }
            }
        }
        Interlocked.Increment(ref _malformedTokens);
    }

    public void Dispose() => Disconnect();
}
