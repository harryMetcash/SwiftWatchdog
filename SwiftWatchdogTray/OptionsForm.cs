using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace SwiftWatchdogTray;

/// <summary>
/// Modal settings dialog launched from the tray context menu. Reads/writes
/// settings over the named pipe via the sendCommand delegate supplied by
/// TrayApplicationContext — the form itself has no pipe knowledge.
/// </summary>
internal sealed class OptionsForm : Form
{
    private readonly CheckBox        _cpuEnabledCheckBox;
    private readonly NumericUpDown   _thresholdUpDown;
    private readonly NumericUpDown   _periodUpDown;
    private readonly DateTimePicker  _restartTimePicker;
    private readonly Button          _saveButton;
    private readonly Button          _cancelButton;

    private readonly Func<string, string> _sendCommand;

    // Mirrors SwiftWatchdog.WatchdogSettings' bounds — duplicated since the tray
    // project doesn't reference the service project (see WatchdogSettingsDto).
    private const double WatchdogSettings_MinCpuThresholdPercent = 1.0;
    private const double WatchdogSettings_MaxCpuThresholdPercent = 1000.0;
    private const int    WatchdogSettings_MinCpuSustainedPeriodSeconds = 10;
    private const int    WatchdogSettings_MaxCpuSustainedPeriodSeconds = 36000;

    public OptionsForm(WatchdogSettingsDto settings, Func<string, string> sendCommand)
    {
        _sendCommand = sendCommand;

        Text = "SwiftWatchdog Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 260);

        var cpuGroup = new GroupBox
        {
            Text = "CPU Monitoring",
            Location = new Point(12, 12),
            Size = new Size(336, 120),
        };
        _cpuEnabledCheckBox = new CheckBox
        {
            Text = "Enable CPU monitoring",
            Location = new Point(12, 24),
            AutoSize = true,
        };
        var thresholdLabel = new Label { Text = "CPU threshold %:", Location = new Point(12, 54), AutoSize = true };
        _thresholdUpDown = new NumericUpDown
        {
            Location = new Point(180, 52), Width = 80,
            Minimum = (decimal)WatchdogSettings_MinCpuThresholdPercent,
            Maximum = (decimal)WatchdogSettings_MaxCpuThresholdPercent,
            DecimalPlaces = 1, Increment = 0.5m,
        };
        var periodLabel = new Label { Text = "Sustained for (seconds):", Location = new Point(12, 84), AutoSize = true };
        _periodUpDown = new NumericUpDown
        {
            Location = new Point(180, 82), Width = 80,
            Minimum = WatchdogSettings_MinCpuSustainedPeriodSeconds,
            Maximum = WatchdogSettings_MaxCpuSustainedPeriodSeconds,
        };
        cpuGroup.Controls.AddRange(new Control[] { _cpuEnabledCheckBox, thresholdLabel, _thresholdUpDown, periodLabel, _periodUpDown });

        var restartGroup = new GroupBox
        {
            Text = "Daily Restart",
            Location = new Point(12, 140),
            Size = new Size(336, 60),
        };
        var restartLabel = new Label { Text = "Restart time:", Location = new Point(12, 26), AutoSize = true };
        _restartTimePicker = new DateTimePicker
        {
            Location = new Point(180, 22), Width = 100,
            Format = DateTimePickerFormat.Time, ShowUpDown = true,
        };
        restartGroup.Controls.AddRange(new Control[] { restartLabel, _restartTimePicker });

        _saveButton   = new Button { Text = "Save",   Location = new Point(192, 210), Width = 75 };
        _cancelButton = new Button { Text = "Cancel", Location = new Point(273, 210), Width = 75 };
        _saveButton.Click += OnSave;
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { cpuGroup, restartGroup, _saveButton, _cancelButton });
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        _cpuEnabledCheckBox.CheckedChanged += (_, _) => UpdateCpuFieldsEnabled();

        LoadFromSettings(settings);
        UpdateCpuFieldsEnabled();
    }

    private void UpdateCpuFieldsEnabled()
    {
        _thresholdUpDown.Enabled = _cpuEnabledCheckBox.Checked;
        _periodUpDown.Enabled    = _cpuEnabledCheckBox.Checked;
    }

    private void LoadFromSettings(WatchdogSettingsDto settings)
    {
        _cpuEnabledCheckBox.Checked = settings.CpuMonitoringEnabled;
        _thresholdUpDown.Value      = (decimal)settings.CpuThresholdPercent;
        _periodUpDown.Value         = settings.CpuSustainedPeriodSeconds;

        if (TimeOnly.TryParseExact(settings.DailyRestartTime, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out TimeOnly t))
        {
            _restartTimePicker.Value = DateTime.Today.Add(t.ToTimeSpan());
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var dto = new WatchdogSettingsDto
        {
            CpuMonitoringEnabled      = _cpuEnabledCheckBox.Checked,
            CpuThresholdPercent       = (double)_thresholdUpDown.Value,
            CpuSustainedPeriodSeconds = (int)_periodUpDown.Value,
            DailyRestartTime          = TimeOnly.FromDateTime(_restartTimePicker.Value)
                                            .ToString("HH:mm", CultureInfo.InvariantCulture),
        };

        string result = _sendCommand($"SETSETTINGS {dto.ToJson()}");
        if (result.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            MessageBox.Show(result, "SwiftWatchdog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
