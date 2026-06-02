using System.Text.Json;
using PokecreatorBot;

namespace pokecreator_setup;

public sealed class MainForm : Form
{
    // Config lives in the same folder the exe is run from.
    private static readonly string ConfigPath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "config.json");

    private readonly TextBox _token = new() { UseSystemPasswordChar = true };
    private readonly TextBox _guild = new();
    private readonly TextBox _channel = new();
    private readonly CheckBox _show = new() { Text = "Show token", AutoSize = true };
    private readonly Button _startStop = new() { Text = "▶ Start Bot", Height = 38 };
    private readonly Label _status = new() { AutoSize = true, Text = "● Stopped", ForeColor = Color.IndianRed };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(15, 15, 26), ForeColor = Color.FromArgb(165, 243, 252),
        Font = new Font("Consolas", 9f), Dock = DockStyle.Fill,
    };

    private readonly BotRunner _runner = new();
    private bool _running;

    public MainForm()
    {
        Text = "PokeCreator Bot — Setup";
        Width = 720;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(26, 26, 46);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(560, 460);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
        for (int i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = $"PokeCreator Discord Bot  v{Updater.AppVersion}",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(167, 139, 250),
            AutoSize = true, Margin = new Padding(0, 0, 0, 4),
        };
        root.Controls.Add(title);

        var help = new LinkLabel
        {
            Text = "Need a token? Click here to open the Discord Developer Portal →",
            AutoSize = true, LinkColor = Color.FromArgb(96, 165, 250), Margin = new Padding(0, 0, 0, 12),
        };
        help.LinkClicked += (_, _) => OpenUrl("https://discord.com/developers/applications");
        root.Controls.Add(help);

        // Token row
        root.Controls.Add(LabeledRow("Bot Token", _token, extra: _show));
        _show.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !_show.Checked;

        // Guild row
        root.Controls.Add(LabeledRow("Server ID (optional — instant commands)", _guild));

        // Channel row
        root.Controls.Add(LabeledRow("Channel ID (optional — bot only replies in this channel)", _channel));

        // Buttons row
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 8, 0, 8) };
        _startStop.Width = 150;
        _startStop.FlatStyle = FlatStyle.Flat;
        _startStop.BackColor = Color.FromArgb(124, 58, 237);
        _startStop.ForeColor = Color.White;
        _startStop.FlatAppearance.BorderSize = 0;
        _startStop.Click += async (_, _) => await ToggleAsync();
        var save = new Button { Text = "Save", Width = 90, Height = 38, FlatStyle = FlatStyle.Flat };
        save.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 120);
        save.Click += (_, _) => { SaveConfig(); Append("Settings saved."); };

        var update = new Button { Text = "⭳ Update", Width = 110, Height = 38, FlatStyle = FlatStyle.Flat };
        update.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 120);
        update.ForeColor = Color.FromArgb(96, 165, 250);
        update.Click += async (_, _) => await CheckForUpdatesAsync();

        buttons.Controls.Add(_startStop);
        buttons.Controls.Add(save);
        buttons.Controls.Add(update);
        buttons.Controls.Add(new Panel { Width = 16, Height = 1 });
        _status.Margin = new Padding(8, 12, 0, 0);
        buttons.Controls.Add(_status);
        root.Controls.Add(buttons);

        // Log
        var logBox = new GroupBox { Text = "Log", Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, Padding = new Padding(8) };
        logBox.Controls.Add(_log);
        root.Controls.Add(logBox);

        _runner.Log += AppendThreadSafe;

        LoadConfig();
        EnsureConfigExists();   // build the config file the moment the app opens

        // Auto-save every setting as the user edits it — no manual Save needed.
        _token.TextChanged += (_, _) => SaveConfig();
        _guild.TextChanged += (_, _) => SaveConfig();
        _channel.TextChanged += (_, _) => SaveConfig();

        FormClosing += async (_, _) => { SaveConfig(); if (_running) await _runner.StopAsync(); };
    }

    private void EnsureConfigExists()
    {
        if (!File.Exists(ConfigPath))
        {
            SaveConfig();
            Append($"Created config file: {ConfigPath}");
        }
    }

    private Control LabeledRow(string label, TextBox box, Control? extra = null)
    {
        var panel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 4, 0, 4) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lbl = new Label { Text = label, AutoSize = true, ForeColor = Color.Silver, Margin = new Padding(0, 0, 0, 2) };
        box.Width = 480;
        box.Dock = DockStyle.Fill;
        box.BackColor = Color.FromArgb(15, 15, 26);
        box.ForeColor = Color.Gainsboro;
        box.BorderStyle = BorderStyle.FixedSingle;
        panel.Controls.Add(lbl, 0, 0);
        panel.SetColumnSpan(lbl, 2);
        panel.Controls.Add(box, 0, 1);
        if (extra != null) panel.Controls.Add(extra, 1, 1);
        return panel;
    }

    private async Task ToggleAsync()
    {
        if (_running)
        {
            _startStop.Enabled = false;
            await _runner.StopAsync();
            _running = false;
            SetStatus(false);
            _startStop.Text = "▶ Start Bot";
            _startStop.Enabled = true;
            return;
        }

        var token = _token.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show("Please paste your bot token first.", "Missing token", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveConfig();
        _startStop.Enabled = false;
        Append("Starting…");
        try
        {
            await _runner.StartAsync(token, _guild.Text.Trim(), _channel.Text.Trim());
            _running = true;
            SetStatus(true);
            _startStop.Text = "■ Stop Bot";
        }
        catch (Exception ex)
        {
            Append("Failed to start: " + ex.Message);
            MessageBox.Show(ex.Message, "Could not start bot", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _startStop.Enabled = true; }
    }

    private async Task CheckForUpdatesAsync()
    {
        Append("Checking for updates…");
        try
        {
            var rel = await Updater.CheckAsync();
            if (rel is null) { Append($"You're up to date (v{Updater.AppVersion})."); return; }

            var ok = MessageBox.Show(
                $"A new version ({rel.Tag}) is available.\nDownload and install it now?\n\nThe app will close, update, and reopen.",
                "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (ok != DialogResult.Yes) return;

            if (_running) await _runner.StopAsync();
            await Updater.DownloadAndApplyAsync(rel, AppendThreadSafe);
        }
        catch (Exception ex)
        {
            Append("Update check failed: " + ex.Message);
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(bool on)
    {
        _status.Text = on ? "● Running" : "● Stopped";
        _status.ForeColor = on ? Color.MediumSeaGreen : Color.IndianRed;
    }

    private void AppendThreadSafe(string line)
    {
        if (InvokeRequired) BeginInvoke(() => Append(line));
        else Append(line);
    }

    private void Append(string line) =>
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");

    // ── config persistence ──
    private sealed record Config(string Token, string Guild, string Channel = "");

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(
                new Config(_token.Text.Trim(), _guild.Text.Trim(), _channel.Text.Trim())));
        }
        catch (Exception ex) { Append("Could not save settings: " + ex.Message); }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath));
            if (cfg is null) return;
            _token.Text = cfg.Token;
            _guild.Text = cfg.Guild;
            _channel.Text = cfg.Channel;
            Append("Loaded saved settings.");
        }
        catch { /* ignore */ }
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
