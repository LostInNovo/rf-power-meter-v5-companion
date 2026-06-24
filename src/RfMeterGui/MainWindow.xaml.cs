using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RfMeterGui;

/// <summary>
/// Main (and only) window. A <see cref="SerialWorker"/> parses the meter's stream on a
/// background thread; a 50 ms UI timer drains its sample bin and fans the data out to the
/// readout, statistics, chart, trigger, and CSV log.
///
/// Command-line flags (used by automated tests, harmless otherwise):
///   --autoconnect    click Connect on startup (default port COM10 if present)
///   --log            start CSV logging on startup
///   --size=WxH       open with the given window size at the top-left of the screen
/// </summary>
public partial class MainWindow : Window
{
    private readonly SerialWorker _meter = new();
    private readonly DispatcherTimer _uiTimer;

    /// <summary>Rolling history of chart points; capacity covers the longest window (5 min at ~20 bins/s).
    /// <see cref="ChartSample"/> is defined by <see cref="SignalChart"/>, which renders this list.</summary>
    private readonly List<ChartSample> _chartBins = new(MaxChartBins + 64);
    private const int MaxChartBins = 6000;

    // Connection status dot colors (toolbar Ellipse): idle grey, connected violet, error red.
    private static readonly Brush DotConnected = new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0xFC));
    private static readonly Brush DotIdle = new SolidColorBrush(Color.FromRgb(0x5B, 0x55, 0x75));
    private static readonly Brush DotError = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    // Running statistics since the last "Reset" click.
    private double _statMinDbm = double.PositiveInfinity;
    private double _statMaxDbm = double.NegativeInfinity;
    private double _statSumDbm;
    private long _statSampleCount;

    /// <summary>Highest dBm seen since the last peak reset; also drawn as a dashed chart line.</summary>
    private double _peakHoldDbm = double.NegativeInfinity;

    // Meter settings, learned from "Read" replies.
    private string? _meterFrequency;                    // last frequency the meter reported (4-digit MHz)
    private bool _settingsBoxesPrefilled;               // the input boxes are filled from the first reply only
    private (int Freq, double Atten)? _pendingSettings; // values just sent, awaiting the meter's echo

    // State for the once-a-second records/s figure and the stream-stall detector.
    private long _lastTotalRecords;
    private DateTime _lastRateUpdateAt = DateTime.UtcNow;
    private DateTime _lastDataReceivedAt = DateTime.UtcNow;

    // CSV logging.
    private StreamWriter? _csvWriter;
    private string? _csvPath;
    private long _csvRowCount;

    /// <summary>Trigger state: armed = waiting for the level to cross the threshold.</summary>
    private bool _triggerArmed = true;

    /// <summary>Consecutive bins above the threshold; the trigger fires on the second (~100 ms debounce).</summary>
    private int _triggerBinsAbove;

    // Noise-floor reference ("tare"). When captured, the readout shows the delta above the
    // floor and the floor-corrected net level (measured watts minus floor watts) — the honest
    // RF contribution when a reading sits within a few dB of the floor.
    // A quick click averages 1 s; press-and-hold averages for the whole hold.
    private double _floorDbm = double.NaN;
    private bool _floorCapturing;
    private DateTime _floorCaptureStartedAt;
    private DateTime? _floorCaptureEndAt;   // null while the button is held: finalized on release
    private DateTime _floorCaptureFinishedAt = DateTime.MinValue; // suppresses the Click that trails a hold
    private double _floorCaptureSum;
    private long _floorCaptureCount;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public MainWindow()
    {
        InitializeComponent();
        _meter.MeterSettingsReceived += OnMeterSettingsReceived;
        _meter.ConnectionFailed += OnSerialConnectionFailed;
        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += OnUiTimerTick;
        PopulatePortList();
        PopulateSampleRateList();

        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                var m = System.Text.RegularExpressions.Regex.Match(arg, @"^--size=(\d+)x(\d+)$");
                if (m.Success)
                {
                    Width = int.Parse(m.Groups[1].Value, Inv);
                    Height = int.Parse(m.Groups[2].Value, Inv);
                    Left = 10;
                    Top = 10;
                }
            }
            if (args.Contains("--autoconnect")) ConnectButton_Click(this, new RoutedEventArgs());
            if (args.Contains("--log")) CsvLogButton_Click(this, new RoutedEventArgs());
        };
    }

    /// <summary>Fill the port dropdown with the system's COM ports, preferring the meter's usual COM10.</summary>
    private void PopulatePortList()
    {
        var names = SerialPort.GetPortNames().Distinct().OrderBy(n => n.Length).ThenBy(n => n).ToList();
        PortCombo.ItemsSource = names;
        if (names.Contains("COM10")) PortCombo.SelectedItem = "COM10";
        else if (names.Count > 0) PortCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Fill the sample-rate dropdown. Periods come from the vendor app's timebase table
    /// (its Sa[] array); the wire rates were measured live per K on 2026-06-12.
    /// Each "Aa" block on the wire is 500 samples taken at the listed period.
    /// </summary>
    private void PopulateSampleRateList()
    {
        int[] samplePeriodUs = { 2, 4, 8, 16, 32, 64, 128, 256, 256, 512, 512, 1024, 2048, 4096, 4096, 8192, 8192, 16384 };
        int[] measuredWireRate = { 3170, 3170, 3100, 3030, 2880, 2630, 2280, 1730, 1760, 1160, 1210, 730, 340, 150, 160, 160, 160, 160 };
        for (int k = 1; k <= 18; k++)
        {
            double kiloSamplesPerSec = 1000.0 / samplePeriodUs[k - 1];
            var rateLabel = kiloSamplesPerSec >= 1 ? $"{kiloSamplesPerSec:0.#} kSa/s" : $"{kiloSamplesPerSec * 1000:0} Sa/s";
            SampleRateCombo.Items.Add($"K{k:00}  {rateLabel} burst, ~{measuredWireRate[k - 1]} rec/s");
        }
    }

    private void RescanPortsButton_Click(object sender, RoutedEventArgs e) => PopulatePortList();

    // Custom title-bar caption buttons (the window runs borderless with WindowChrome).
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : ""; // restore / maximize glyph
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Toggle the connection. On connect, also poll the meter's settings once.</summary>
    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_meter.IsOpen)
        {
            DisconnectAndResetUi();
            return;
        }
        if (PortCombo.SelectedItem is not string port)
        {
            ConnectionStatusText.Text = "no port selected";
            return;
        }
        if (!int.TryParse(BaudRateBox.Text.Trim(), out var baud) || baud <= 0) baud = 460800;
        try
        {
            _meter.Connect(port, baud);
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = $"open failed: {ex.Message}";
            return;
        }
        ConnectButton.Content = "Disconnect";
        PortCombo.IsEnabled = BaudRateBox.IsEnabled = RescanPortsButton.IsEnabled = false;
        ReadFromMeterButton.IsEnabled = SampleRateCombo.IsEnabled = true;
        ConnectionStatusText.Text = $"{port} @ {baud}";
        StatusDot.Fill = DotConnected;
        _lastTotalRecords = 0;
        _lastRateUpdateAt = _lastDataReceivedAt = DateTime.UtcNow;
        _uiTimer.Start();

        // Give the stream a moment to start, then ask for the current frequency/attenuation.
        await Task.Delay(400);
        if (_meter.IsOpen) TryRequestMeterSettings();
    }

    /// <summary>
    /// Close the port, stop logging, and return every control to its disconnected state.
    /// An in-progress floor capture is finalized with whatever it has collected so far.
    /// </summary>
    private void DisconnectAndResetUi()
    {
        _uiTimer.Stop();
        _meter.Disconnect();
        StopCsvLogging();
        if (_floorCapturing) FinalizeFloorCapture();
        ConnectButton.Content = "Connect";
        PortCombo.IsEnabled = BaudRateBox.IsEnabled = RescanPortsButton.IsEnabled = true;
        ReadFromMeterButton.IsEnabled = SendToMeterButton.IsEnabled = SampleRateCombo.IsEnabled = false;
        ConnectionStatusText.Text = "disconnected";
        StatusDot.Fill = DotIdle;
    }

    /// <summary>Serial thread died (cable pulled, port stolen, …) — reflect that in the UI.</summary>
    private void OnSerialConnectionFailed(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            DisconnectAndResetUi();
            ConnectionStatusText.Text = $"port error: {message}";
            StatusDot.Fill = DotError;
        });
    }

    /// <summary>
    /// The meter answered a "Read": show its settings, pre-fill the input boxes once, and
    /// if we just sent new settings, verify the echo matches what we sent.
    /// </summary>
    private void OnMeterSettingsReceived(string frequency, string attenuation)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _meterFrequency = frequency;
            MeterReportsText.Text = $"Meter reports: {int.Parse(frequency, Inv)} MHz, {attenuation} dB";
            SendToMeterButton.IsEnabled = true;
            if (!_settingsBoxesPrefilled)
            {
                _settingsBoxesPrefilled = true;
                FrequencyBox.Text = frequency;
                AttenuationBox.Text = double.Parse(attenuation, Inv).ToString("0.0", Inv);
            }
            if (_pendingSettings.HasValue)
            {
                var (sentFreq, sentAtten) = _pendingSettings.Value;
                _pendingSettings = null;
                var ok = int.Parse(frequency, Inv) == sentFreq && Math.Abs(double.Parse(attenuation, Inv) - sentAtten) < 0.05;
                MeterSettingsStatusText.Text = ok
                    ? $"applied: {int.Parse(frequency, Inv)} MHz, {attenuation} dB"
                    : $"MISMATCH: sent {sentFreq} MHz {sentAtten.ToString("+00.0;-00.0", Inv)}, meter reports {frequency} MHz {attenuation}";
                MeterSettingsStatusText.Foreground = ok ? Brushes.LightGreen : Brushes.OrangeRed;
            }
        });
    }

    /// <summary>Poll the meter's settings, reporting failures in the status line instead of throwing.</summary>
    private void TryRequestMeterSettings()
    {
        try { _meter.RequestMeterSettings(); }
        catch (Exception ex) { ConnectionStatusText.Text = $"poll failed: {ex.Message}"; }
    }

    private void ReadFromMeterButton_Click(object sender, RoutedEventArgs e) => TryRequestMeterSettings();

    /// <summary>
    /// Validate the frequency/attenuation boxes, write them to the meter, then poll it back
    /// half a second later so <see cref="OnMeterSettingsReceived"/> can confirm the echo.
    /// </summary>
    private async void SendToMeterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_meterFrequency is null)
        {
            ShowSettingsError("meter not read yet — click 'Read from meter' first");
            return;
        }
        if (!int.TryParse(FrequencyBox.Text.Trim(), NumberStyles.Integer, Inv, out var frequency) || frequency is < 1 or > 9999)
        {
            ShowSettingsError("frequency must be 1..9999 MHz");
            return;
        }
        if (!TryParseUserNumber(AttenuationBox.Text, out var attenuation))
        {
            ShowSettingsError("invalid attenuation");
            return;
        }
        if (Math.Abs(attenuation) > 99.9)
        {
            ShowSettingsError("attenuation range is -99.9 … +99.9 dB");
            return;
        }
        try
        {
            _pendingSettings = (frequency, Math.Round(attenuation, 1));
            _meter.SendFrequencyAndAttenuation(frequency, attenuation);
            MeterSettingsStatusText.Text = "sent, verifying…";
            MeterSettingsStatusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            _pendingSettings = null;
            ShowSettingsError($"send failed: {ex.Message}");
            return;
        }
        await Task.Delay(500);
        if (_meter.IsOpen) TryRequestMeterSettings();
        // The poll right after an A command is sometimes swallowed by the meter's fragile
        // parser; if the echo hasn't arrived, ask once more.
        await Task.Delay(1500);
        if (_meter.IsOpen && _pendingSettings.HasValue) TryRequestMeterSettings();
    }

    /// <summary>Show an error in the meter-settings status line.</summary>
    private void ShowSettingsError(string message)
    {
        MeterSettingsStatusText.Text = message;
        MeterSettingsStatusText.Foreground = Brushes.OrangeRed;
    }

    /// <summary>Send the chosen sample rate. Write-only: confirmation is the rec/s figure changing.</summary>
    private void SampleRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_meter.IsOpen || SampleRateCombo.SelectedIndex < 0) return;
        var kIndex = SampleRateCombo.SelectedIndex + 1;
        try
        {
            _meter.SendSampleRate(kIndex);
            MeterSettingsStatusText.Text = $"rate K{kIndex:00} sent — stream pauses ~1 s, then check rec/s in the status line";
            MeterSettingsStatusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            ShowSettingsError($"rate send failed: {ex.Message}");
        }
    }

    private void PeakResetButton_Click(object sender, RoutedEventArgs e)
    {
        _peakHoldDbm = double.NegativeInfinity;
        PeakHoldText.Text = "Peak hold: —";
    }

    /// <summary>Mouse press: start an open-ended floor capture that runs until release.</summary>
    private void FloorCaptureButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_meter.IsOpen)
        {
            FloorDeltaText.Text = "connect first";
            return;
        }
        BeginFloorCapture(openEnded: true);
    }

    /// <summary>
    /// Mouse release: a long hold finalizes immediately with everything averaged so far;
    /// a quick click lets the capture run on to the classic 1 s total.
    /// </summary>
    private void FloorCaptureButton_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_floorCapturing) return;
        if (DateTime.UtcNow - _floorCaptureStartedAt >= TimeSpan.FromSeconds(1)) FinalizeFloorCapture();
        else _floorCaptureEndAt = _floorCaptureStartedAt.AddSeconds(1);
    }

    /// <summary>
    /// Keyboard activation (Space/Enter): plain 1 s capture. A mouse click also raises this
    /// right after the MouseDown/MouseUp pair has handled it — the capturing/just-finished
    /// guards make it a no-op in that case.
    /// </summary>
    private void FloorCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_floorCapturing) return;
        if ((DateTime.UtcNow - _floorCaptureFinishedAt).TotalMilliseconds < 300) return;
        if (!_meter.IsOpen)
        {
            FloorDeltaText.Text = "connect first";
            return;
        }
        BeginFloorCapture(openEnded: false);
    }

    /// <summary>Reset the accumulators and start a capture (open-ended or fixed 1 s).</summary>
    private void BeginFloorCapture(bool openEnded)
    {
        _floorCapturing = true;
        _floorCaptureStartedAt = DateTime.UtcNow;
        _floorCaptureEndAt = openEnded ? null : _floorCaptureStartedAt.AddSeconds(1);
        _floorCaptureSum = 0;
        _floorCaptureCount = 0;
        FloorText.Text = "Floor: capturing…";
        FloorDeltaText.Text = "keep the signal source OFF while capturing";
    }

    /// <summary>Average everything captured so far into the floor reference.</summary>
    private void FinalizeFloorCapture()
    {
        _floorCapturing = false;
        _floorCaptureFinishedAt = DateTime.UtcNow;
        if (_floorCaptureCount == 0)
        {
            FloorText.Text = "Floor: no data";
            return;
        }
        _floorDbm = _floorCaptureSum / _floorCaptureCount;
        var seconds = (DateTime.UtcNow - _floorCaptureStartedAt).TotalSeconds;
        FloorText.Text = $"Floor: {_floorDbm.ToString("+0.0;-0.0", Inv)} dBm ({seconds.ToString("0.0", Inv)} s avg)";
        FloorDeltaText.Text = "";
        FloorClearButton.IsEnabled = true;
    }

    /// <summary>Drop the floor reference and go back to plain absolute readings.</summary>
    private void FloorClearButton_Click(object sender, RoutedEventArgs e)
    {
        _floorDbm = double.NaN;
        FloorText.Text = "Floor: —";
        FloorDeltaText.Text = "";
        FloorClearButton.IsEnabled = false;
    }

    /// <summary>
    /// Per-tick part of the floor feature: accumulate the capture average while one is
    /// running (showing the elapsed time), then keep the delta/net line current.
    /// Net power = measured − floor in watts; below ~0.2 dB of delta the subtraction is
    /// all jitter, so show "at floor".
    /// </summary>
    private void UpdateFloorReadout(BinSnapshot bin, double avgDbm)
    {
        if (_floorCapturing)
        {
            _floorCaptureSum += bin.Sum;
            _floorCaptureCount += bin.Count;
            var now = DateTime.UtcNow;
            FloorText.Text = $"Floor: capturing… {(now - _floorCaptureStartedAt).TotalSeconds.ToString("0.0", Inv)} s";
            if (_floorCaptureEndAt.HasValue && now >= _floorCaptureEndAt.Value) FinalizeFloorCapture();
            return;
        }
        if (double.IsNaN(_floorDbm)) return;

        double deltaDb = avgDbm - _floorDbm;
        double netWatts = DbmToWatts(avgDbm) - DbmToWatts(_floorDbm);
        if (deltaDb < 0.2 || netWatts <= 0)
        {
            FloorDeltaText.Text = $"Δ {deltaDb.ToString("+0.0;-0.0", Inv)} dB — at/below floor";
        }
        else
        {
            double netDbm = WattsToDbm(netWatts);
            FloorDeltaText.Text = $"Δ {deltaDb.ToString("+0.0", Inv)} dB above floor — net {netDbm.ToString("+0.0;-0.0", Inv)} dBm ({FormatWatts(netWatts)})";
        }
    }

    private void StatsResetButton_Click(object sender, RoutedEventArgs e)
    {
        _statMinDbm = double.PositiveInfinity;
        _statMaxDbm = double.NegativeInfinity;
        _statSumDbm = 0;
        _statSampleCount = 0;
        StatMinText.Text = "Min:  —";
        StatMaxText.Text = "Max:  —";
        StatAvgText.Text = "Avg:  —";
    }

    private void TriggerClearButton_Click(object sender, RoutedEventArgs e) => TriggerCapturesList.Items.Clear();

    /// <summary>Toggle CSV logging to Documents\rfmeter\logs\rf_&lt;timestamp&gt;.csv.</summary>
    private void CsvLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_csvWriter != null)
        {
            StopCsvLogging();
            return;
        }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "rfmeter", "logs");
            Directory.CreateDirectory(dir);
            _csvPath = Path.Combine(dir, $"rf_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _csvWriter = new StreamWriter(_csvPath, false) { AutoFlush = false };
            _csvWriter.WriteLine("timestamp,avg_dbm,min_dbm,max_dbm,samples");
            _csvRowCount = 0;
            CsvLogButton.Content = "Stop";
            CsvLogStatusText.Text = Path.GetFileName(_csvPath);
        }
        catch (Exception ex)
        {
            CsvLogStatusText.Text = $"log failed: {ex.Message}";
        }
    }

    /// <summary>Flush and close the CSV file, if one is open.</summary>
    private void StopCsvLogging()
    {
        if (_csvWriter == null) return;
        try { _csvWriter.Flush(); _csvWriter.Dispose(); } catch { }
        _csvWriter = null;
        CsvLogButton.Content = "Start";
        CsvLogStatusText.Text = $"saved {_csvRowCount} rows: {Path.GetFileName(_csvPath)}";
    }

    /// <summary>
    /// 50 ms heartbeat: drain the serial worker's sample bin and update the readout, peak
    /// hold, statistics, CSV log, trigger, records/s figure, and chart.
    /// </summary>
    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        var bin = _meter.DrainBin();
        var now = DateTime.UtcNow;

        if (bin.Count > 0)
        {
            _lastDataReceivedAt = now;
            var avg = bin.Avg;

            _chartBins.Add(new ChartSample(now, avg, bin.Min, bin.Max));
            if (_chartBins.Count > MaxChartBins) _chartBins.RemoveRange(0, _chartBins.Count - MaxChartBins);

            DbmReadoutText.Text = avg.ToString("+0.0;-0.0", Inv);
            WattsReadoutText.Text = FormatWatts(DbmToWatts(avg));
            SignalLevelBar.Value = Math.Clamp(avg, SignalLevelBar.Minimum, SignalLevelBar.Maximum);

            if (bin.Max > _peakHoldDbm)
            {
                _peakHoldDbm = bin.Max;
                PeakHoldText.Text = $"Peak hold: {_peakHoldDbm.ToString("+0.0;-0.0", Inv)} dBm  ({FormatWatts(DbmToWatts(_peakHoldDbm))})";
            }

            _statSumDbm += bin.Sum;
            _statSampleCount += bin.Count;
            if (bin.Min < _statMinDbm) _statMinDbm = bin.Min;
            if (bin.Max > _statMaxDbm) _statMaxDbm = bin.Max;
            StatMinText.Text = $"Min:  {_statMinDbm.ToString("+0.0;-0.0", Inv)} dBm";
            StatMaxText.Text = $"Max:  {_statMaxDbm.ToString("+0.0;-0.0", Inv)} dBm";
            StatAvgText.Text = $"Avg:  {(_statSumDbm / _statSampleCount).ToString("+0.00;-0.00", Inv)} dBm";

            if (_csvWriter != null)
            {
                _csvWriter.WriteLine(string.Create(Inv,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{avg:F2},{bin.Min:F1},{bin.Max:F1},{bin.Count}"));
                if (++_csvRowCount % 20 == 0) _csvWriter.Flush();
            }

            UpdateFloorReadout(bin, avg);
            ProcessTriggerCapture(avg);
        }
        else if (_meter.IsOpen && (now - _lastDataReceivedAt).TotalSeconds > 2)
        {
            // The stream stalled (e.g. while the meter reconfigures after a settings command).
            DbmReadoutText.Text = "--.-";
            WattsReadoutText.Text = "no data";
        }

        // Once a second, refresh the records/s figure in the status line.
        if ((now - _lastRateUpdateAt).TotalSeconds >= 1.0)
        {
            var total = _meter.TotalRecords;
            var recordsPerSec = (total - _lastTotalRecords) / (now - _lastRateUpdateAt).TotalSeconds;
            _lastTotalRecords = total;
            _lastRateUpdateAt = now;
            if (_meter.IsOpen)
            {
                var bad = _meter.MalformedTokens;
                ConnectionStatusText.Text = $"{PortCombo.SelectedItem} @ {BaudRateBox.Text} — {recordsPerSec:F0} rec/s"
                                            + (bad > 1 ? $", {bad} bad tokens" : "");
            }
        }

        TimeSeriesChart.Update(_chartBins, now, SelectedChartWindowSeconds(), MaxTraceCheck.IsChecked == true, _peakHoldDbm);
    }

    /// <summary>
    /// Threshold trigger: when armed and the level stays above the threshold for two
    /// consecutive bins (~100 ms — debounces single-bin noise spikes), capture one
    /// timestamped datapoint, then stay quiet until the level drops 2 dB below the
    /// threshold again.
    /// </summary>
    private void ProcessTriggerCapture(double avg)
    {
        if (TriggerEnableCheck.IsChecked != true) { _triggerArmed = true; _triggerBinsAbove = 0; return; }
        if (!TryParseUserNumber(TriggerThresholdBox.Text, out var threshold)) return;

        if (_triggerArmed)
        {
            if (avg < threshold)
            {
                _triggerBinsAbove = 0;
            }
            else if (++_triggerBinsAbove >= 2)
            {
                _triggerArmed = false;
                _triggerBinsAbove = 0;
                TriggerCapturesList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.f}  {avg.ToString("+0.0;-0.0", Inv)} dBm");
                if (TriggerCapturesList.Items.Count > 200) TriggerCapturesList.Items.RemoveAt(TriggerCapturesList.Items.Count - 1);
                TriggerStatusText.Text = "captured — re-arms 2 dB below threshold";
            }
        }
        else if (avg < threshold - 2.0)
        {
            _triggerArmed = true;
            TriggerStatusText.Text = "armed";
        }
    }

    /// <summary>
    /// Parse a number the user typed, accepting either '.' or ',' as the decimal separator
    /// (invariant culture first, then the local culture). False on blank/garbage.
    /// </summary>
    private static bool TryParseUserNumber(string text, out double value)
    {
        text = text.Trim();
        return double.TryParse(text, NumberStyles.Float, Inv, out value) || double.TryParse(text, out value);
    }

    /// <summary>dBm to watts: P = 10^((dBm − 30) / 10).</summary>
    private static double DbmToWatts(double dbm) => Math.Pow(10.0, (dbm - 30.0) / 10.0);

    /// <summary>Watts to dBm: dBm = 10·log10(P) + 30.</summary>
    private static double WattsToDbm(double watts) => 10.0 * Math.Log10(watts) + 30.0;

    /// <summary>Format a power in watts with an auto-scaled unit (W / mW / uW / nW / pW).</summary>
    private static string FormatWatts(double watts)
    {
        return watts switch
        {
            >= 1.0 => $"{watts.ToString("F3", Inv)} W",
            >= 1e-3 => $"{(watts * 1e3).ToString("F3", Inv)} mW",
            >= 1e-6 => $"{(watts * 1e6).ToString("F3", Inv)} uW",
            >= 1e-9 => $"{(watts * 1e9).ToString("F3", Inv)} nW",
            _ => $"{(watts * 1e12).ToString("F3", Inv)} pW",
        };
    }

    /// <summary>The chart window selected in the dropdown, in seconds.</summary>
    private int SelectedChartWindowSeconds() => ChartWindowCombo.SelectedIndex switch
    {
        0 => 10,
        1 => 30,
        3 => 300,
        _ => 60,
    };

    /// <summary>Shut everything down cleanly when the window closes.</summary>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _uiTimer.Stop();
        StopCsvLogging();
        _meter.Dispose();
    }
}
