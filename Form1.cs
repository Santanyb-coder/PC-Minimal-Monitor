using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor
{
    public class DriveItem { public string Name; public float? Temp, Load; }
    public class FanItem { public string Name; public float Rpm; public float? Pct; }

    public class AppSettings
    {
        public int IntervalMs { get; set; } = 1000;
        public bool MinimizeToTray { get; set; } = true;
        public bool OvEnabled { get; set; } = false;
        public int OvCorner { get; set; } = 0;
        public int OvCustomX { get; set; } = 20;
        public int OvCustomY { get; set; } = 20;
        public bool OvVertical { get; set; } = false;
        public int OvBgMode { get; set; } = 0;
        public int OvColorArgb { get; set; } = Color.FromArgb(0, 220, 140).ToArgb();
        public int OvFontSize { get; set; } = 10;
        public bool OvShowVram { get; set; } = true;
        public bool OvShowTime { get; set; } = false;
        public int ThemeIndex { get; set; } = 0;
        public bool OverheatAlert { get; set; } = true;
        public float UiScale { get; set; } = 1.0f;
    }

    public partial class Form1 : Form
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX { public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)), dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private AppSettings _cfg = new();
        private string _cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcMinimalMonitor", "settings.json");
        private Computer _comp, _mbComp; private string _mbErr = "ОК";
        private System.Windows.Forms.Timer _timer;
        private List<float> _cpuHist = new(), _gpuHist = new();
        private const int MaxPts = 60, TopOff = 64;
        private float _cpuT, _gpuT, _cpuL, _gpuL, _ramU, _ramTot, _ramPct, _cpuMaxT, _gpuMaxT, _cpuClock, _cpuPower, _gpuClock, _gpuPower, _vramUsed, _vramTotal;
        private float? _ramT;
        private string _cpuName = "ПРОЦЕССОР (CPU)", _gpuName = "ВИДЕОКАРТА (GPU)", _topCpu = "—", _topRam = "—";
        private List<DriveItem> _drives = new();
        private List<FanItem> _fans = new();
        private long _prevBytesRx, _prevBytesTx;
        private DateTime _prevNetCheck = DateTime.UtcNow, _lastAlert = DateTime.MinValue, _prevCheck = DateTime.UtcNow;
        private double _netDownMb, _netUpMb;
        private Dictionary<int, TimeSpan> _prevProc = new();
        private int _tab = 0;
        private List<Button> _tabBtns = new();
        private Panel _tabPanel;
        private NotifyIcon _tray;
        private ToolStripMenuItem _trayTgl, _autoTop, _autoTray, _ovTgl, _ovTrayTgl, _alertTgl, _ovVramTgl, _ovTimeTgl;
        private List<ToolStripMenuItem> _spdItems = new(), _posItems = new(), _colItems = new(), _bgItems = new(), _fontItems = new(), _themeItems = new(), _scaleItems = new();
        private bool _forceClose = false;
        private OverlayForm _ov;
        private const string TaskName = "PcMinimalMonitorAutostart";
        public Form1()
        {
            LoadSettings();
            Text = "PC Minimal Monitor  v1.1  •  by SantaNyb";
            BackColor = Color.FromArgb(20, 20, 24); DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            InitTabs(); InitMenu(); InitTray();
            ApplyUiScale(_cfg.UiScale, false);

            _comp = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
            try { _comp.Open(); } catch { }
            try { _comp.IsStorageEnabled = true; } catch { }
            
            try { _comp.IsStorageEnabled = true; } catch { }
            
            try { _comp.IsMotherboardEnabled = true; } catch { }
            try { _comp.IsControllerEnabled = true; } catch { }
            try { _comp.IsMotherboardEnabled = true; } catch { }

            _ov = new OverlayForm(_cfg, () => SaveSettings());
            if (_cfg.OvEnabled) _ov.Show();
            RegisterHotKey(this.Handle, 1, 0x0001, 0x48);

            _timer = new System.Windows.Forms.Timer { Interval = _cfg.IntervalMs };
            _timer.Tick += (s, e) => UpdateSensors();
            _timer.Start();
            UpdateSensors();
            if (Environment.GetCommandLineArgs().Contains("--minimized")) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Hide(); }
        }

        private void ApplyUiScale(float scale, bool save = true)
        {
            _cfg.UiScale = scale;
            ClientSize = new Size((int)(634 * scale), (int)((500 + TopOff) * scale));
            if (_tabPanel != null)
            {
                _tabPanel.Height = (int)(36 * scale);
                int[] widths = { 120, 115, 170 };
                int x = (int)(20 * scale);
                for (int i = 0; i < _tabBtns.Count; i++)
                {
                    _tabBtns[i].Location = new Point(x, (int)(4 * scale));
                    _tabBtns[i].Size = new Size((int)(widths[i] * scale), (int)(28 * scale));
                    _tabBtns[i].Font = new Font("Segoe UI", 9.2f * scale, FontStyle.Bold);
                    x += (int)((widths[i] + 6) * scale);
                }
            }
            if (save) SaveSettings();
            Invalidate();
        }

        private void LoadSettings() { try { if (File.Exists(_cfgPath)) _cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_cfgPath)) ?? new AppSettings(); } catch { _cfg = new AppSettings(); } }
        private void SaveSettings() { try { Directory.CreateDirectory(Path.GetDirectoryName(_cfgPath)!); File.WriteAllText(_cfgPath, JsonSerializer.Serialize(_cfg)); } catch { } }
        private (Color Cpu, Color Gpu, Color Ram) GetTheme() => _cfg.ThemeIndex switch { 1 => (Color.FromArgb(220, 225, 235), Color.FromArgb(165, 175, 190), Color.FromArgb(190, 195, 205)), 2 => (Color.FromArgb(255, 65, 85), Color.FromArgb(255, 145, 45), Color.FromArgb(220, 50, 110)), _ => (Color.FromArgb(0, 185, 255), Color.FromArgb(50, 215, 110), Color.FromArgb(180, 110, 255)) };
        private Color GetTempCol(float t, Color b) => t >= 80 ? Color.FromArgb(255, 70, 70) : (t >= 65 ? Color.FromArgb(255, 165, 45) : b);

        private void InitTabs()
        {
            _tabPanel = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(24, 24, 30) };
            string[] names = { "   Главное", "   Диски", "    Система и Сеть" }; int[] widths = { 120, 115, 170 }; int x = 20;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var btn = new Button { Text = names[i], Location = new Point(x, 4), Size = new Size(widths[i], 28), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.2f, FontStyle.Bold), Cursor = Cursors.Hand, TextAlign = ContentAlignment.MiddleCenter };
                btn.FlatAppearance.BorderSize = 0;
                btn.Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; DrawIcon(e.Graphics, idx == 2 ? 6 : idx, (int)(10 * _cfg.UiScale), (int)(7 * _cfg.UiScale), idx == _tab ? GetTheme().Cpu : Color.FromArgb(160, 160, 175)); };
                btn.Click += (s, e) => { _tab = idx; RefreshTabs(); Invalidate(); };
                _tabBtns.Add(btn); _tabPanel.Controls.Add(btn); x += widths[i] + 6;
            }
            Controls.Add(_tabPanel); RefreshTabs();
        }

        private void RefreshTabs()
        {
            var ac = GetTheme().Cpu;
            for (int k = 0; k < _tabBtns.Count; k++) { _tabBtns[k].BackColor = k == _tab ? Color.FromArgb(42, 44, 54) : Color.FromArgb(24, 24, 30); _tabBtns[k].ForeColor = k == _tab ? ac : Color.FromArgb(160, 160, 175); _tabBtns[k].Invalidate(); }
        }

        private ToolStripMenuItem MkItem(string txt, bool chk, Action onClick) { var it = new ToolStripMenuItem(txt) { Checked = chk }; it.Click += (s, e) => onClick(); return it; }
        private void InitMenu()
        {
            var ms = new MenuStrip { BackColor = Color.FromArgb(28, 28, 34), ForeColor = Color.White, Renderer = new DarkRenderer(), Dock = DockStyle.Top };
            var set = new ToolStripMenuItem("⚙ Настройки");
            var spd = new ToolStripMenuItem("Скорость обновления");
            foreach (var (lbl, val) in new[] { ("0.5 секунды", 500), ("1 секунда", 1000), ("5 секунд", 5000), ("10 секунд", 10000), ("30 секунд", 30000), ("1 минута", 60000), ("2 минуты", 120000) })
            {
                ToolStripMenuItem it = null; it = MkItem(lbl, val == _cfg.IntervalMs, () => { _cfg.IntervalMs = val; _timer.Interval = val; _spdItems.ForEach(x => x.Checked = false); it.Checked = true; SaveSettings(); });
                _spdItems.Add(it); spd.DropDownItems.Add(it);
            }

            var sclM = new ToolStripMenuItem("Масштаб окна");
            foreach (var (sName, sVal) in new[] { ("100% (Стандарт)", 1.0f), ("125% (Для 2K / 4K экранов)", 1.25f) })
            {
                ToolStripMenuItem it = null; it = MkItem(sName, Math.Abs(_cfg.UiScale - sVal) < 0.01f, () => { ApplyUiScale(sVal); _scaleItems.ForEach(x => x.Checked = false); it.Checked = true; });
                _scaleItems.Add(it); sclM.DropDownItems.Add(it);
            }

            var ovM = new ToolStripMenuItem("Игровой оверлей");
            _ovTgl = MkItem("Включить оверлей", _cfg.OvEnabled, ToggleOverlay); _ovTgl.ShortcutKeyDisplayString = "Alt+H";

            var bgM = new ToolStripMenuItem("Фон оверлея");
            foreach (var (bN, bM) in new[] { ("Полупрозрачная дымка", 0), ("Плотный чёрный", 1), ("Полностью прозрачный", 2) })
            {
                ToolStripMenuItem it = null; it = MkItem(bN, _cfg.OvBgMode == bM, () => { _cfg.OvBgMode = bM; _ov.ApplyStyle(_cfg); _bgItems.ForEach(x => x.Checked = false); it.Checked = true; SaveSettings(); });
                _bgItems.Add(it); bgM.DropDownItems.Add(it);
            }

            var posM = new ToolStripMenuItem("Позиция на экране");
            string[] cNames = { "Верхний левый угол", "Верхний правый угол", "Нижний левый угол", "Нижний правый угол", "Свободно (перетащить мышью)" };
            for (int i = 0; i < cNames.Length; i++)
            {
                int c = i; ToolStripMenuItem it = null; it = MkItem(cNames[i], _cfg.OvCorner == c, () => { _cfg.OvCorner = c; _ov.ApplyStyle(_cfg); _posItems.ForEach(x => x.Checked = false); it.Checked = true; UpdateOverlay(); SaveSettings(); });
                _posItems.Add(it); posM.DropDownItems.Add(it);
            }

            var styM = new ToolStripMenuItem("Вид отображения");
            ToolStripMenuItem rIt = null, cIt = null;
            rIt = MkItem("В одну строчку", !_cfg.OvVertical, () => { _cfg.OvVertical = false; rIt.Checked = true; cIt.Checked = false; UpdateOverlay(); SaveSettings(); });
            cIt = MkItem("Столбиком", _cfg.OvVertical, () => { _cfg.OvVertical = true; cIt.Checked = true; rIt.Checked = false; UpdateOverlay(); SaveSettings(); });
            styM.DropDownItems.AddRange(new ToolStripItem[] { rIt, cIt });

            var colM = new ToolStripMenuItem("Цвет шрифта");
            foreach (var (cN, cV) in new[] { ("Мятно-зелёный", Color.FromArgb(0, 220, 140)), ("Неоново-голубой", Color.FromArgb(0, 195, 255)), ("Белый", Color.White), ("Ярко-жёлтый", Color.FromArgb(255, 215, 0)), ("Кораллово-красный", Color.FromArgb(255, 95, 95)), ("Сиреневый", Color.FromArgb(190, 130, 255)), ("Чёрный (с обводкой)", Color.FromArgb(15, 15, 18)) })
            {
                ToolStripMenuItem it = null; it = MkItem(cN, cV.ToArgb() == _cfg.OvColorArgb, () => { _cfg.OvColorArgb = cV.ToArgb(); _ov.ApplyStyle(_cfg); _colItems.ForEach(x => x.Checked = false); it.Checked = true; SaveSettings(); });
                _colItems.Add(it); colM.DropDownItems.Add(it);
            }

            var fntM = new ToolStripMenuItem("Размер шрифта");
            foreach (var (fN, fS) in new[] { ("Мелкий (1080p)", 9), ("Средний (стандарт)", 10), ("Крупный (2K / 4K)", 13) })
            {
                ToolStripMenuItem it = null; it = MkItem(fN, _cfg.OvFontSize == fS, () => { _cfg.OvFontSize = fS; _ov.ApplyStyle(_cfg); _fontItems.ForEach(x => x.Checked = false); it.Checked = true; UpdateOverlay(); SaveSettings(); });
                _fontItems.Add(it); fntM.DropDownItems.Add(it);
            }

            _ovVramTgl = MkItem("Показывать видеопамять (VRAM)", _cfg.OvShowVram, () => { _cfg.OvShowVram = !_cfg.OvShowVram; _ovVramTgl.Checked = _cfg.OvShowVram; UpdateOverlay(); SaveSettings(); });
            _ovTimeTgl = MkItem("Показывать текущие часы", _cfg.OvShowTime, () => { _cfg.OvShowTime = !_cfg.OvShowTime; _ovTimeTgl.Checked = _cfg.OvShowTime; UpdateOverlay(); SaveSettings(); });
            ovM.DropDownItems.AddRange(new ToolStripItem[] { _ovTgl, new ToolStripSeparator(), bgM, posM, styM, colM, fntM, new ToolStripSeparator(), _ovVramTgl, _ovTimeTgl });

            var thmM = new ToolStripMenuItem("Тема оформления");
            string[] thms = { "Киберпанк (Неон)", "Строгий монохром", "ROG Красный" };
            for (int i = 0; i < thms.Length; i++)
            {
                int t = i; ToolStripMenuItem it = null; it = MkItem(thms[i], _cfg.ThemeIndex == t, () => { _cfg.ThemeIndex = t; _themeItems.ForEach(x => x.Checked = false); it.Checked = true; RefreshTabs(); Invalidate(); SaveSettings(); });
                _themeItems.Add(it); thmM.DropDownItems.Add(it);
            }

            _alertTgl = MkItem("Уведомлять при перегреве (>85°C)", _cfg.OverheatAlert, () => { _cfg.OverheatAlert = !_cfg.OverheatAlert; _alertTgl.Checked = _cfg.OverheatAlert; SaveSettings(); });
            _trayTgl = MkItem("Сворачивать в трей при закрытии", _cfg.MinimizeToTray, () => { _cfg.MinimizeToTray = !_cfg.MinimizeToTray; _trayTgl.Checked = _cfg.MinimizeToTray; _tray.Visible = _cfg.MinimizeToTray; SaveSettings(); });
            _autoTop = MkItem("Запускать вместе с Windows", IsAuto(), ToggleAuto);
            var rst = MkItem("Сбросить пики (Макс. °C)", false, () => { _cpuMaxT = _cpuT; _gpuMaxT = _gpuT; Invalidate(); });
            var cpy = MkItem("Скопировать отчёт в буфер", false, CopyReport);
            var abt = MkItem("О программе", false, () => MessageBox.Show("PC Minimal Monitor — Версия 1.1 (Release)\n\nЛёгкий системный монитор температур, частот и нагрузки в реальном времени.\n\nРазработчик: SantaNyb\nТехнологии: C# / .NET 8 / LibreHardwareMonitor\nГорячая клавиша оверлея: Alt + H", "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information));
            var ex = MkItem("Выход из программы", false, () => { _forceClose = true; Application.Exit(); });

            set.DropDownItems.AddRange(new ToolStripItem[] { spd, sclM, ovM, thmM, new ToolStripSeparator(), _alertTgl, _trayTgl, _autoTop, new ToolStripSeparator(), rst, cpy, new ToolStripSeparator(), abt, ex });
            ms.Items.Add(set); MainMenuStrip = ms; Controls.Add(ms);
        }
        private void InitTray()
        {
            var cm = new ContextMenuStrip { Renderer = new DarkRenderer() };
            var op = MkItem("Открыть монитор", false, () => { Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate(); }); op.Font = new Font(Font, FontStyle.Bold);
            _ovTrayTgl = MkItem("Показывать оверлей", _cfg.OvEnabled, ToggleOverlay); _ovTrayTgl.ShortcutKeyDisplayString = "Alt+H";
            _autoTray = MkItem("Запускать вместе с Windows", IsAuto(), ToggleAuto);
            var ex = MkItem("Выход", false, () => { _forceClose = true; Application.Exit(); });
            cm.Items.AddRange(new ToolStripItem[] { op, new ToolStripSeparator(), _ovTrayTgl, _autoTray, new ToolStripSeparator(), ex });
            _tray = new NotifyIcon { Icon = Icon, Text = "PC Minimal Monitor", ContextMenuStrip = cm, Visible = _cfg.MinimizeToTray };
            _tray.DoubleClick += (s, e) => { Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate(); };
        }

        private void ToggleOverlay() { _cfg.OvEnabled = !_cfg.OvEnabled; if (_ovTgl != null) _ovTgl.Checked = _cfg.OvEnabled; if (_ovTrayTgl != null) _ovTrayTgl.Checked = _cfg.OvEnabled; if (_cfg.OvEnabled) { UpdateOverlay(); _ov.Show(); } else _ov.Hide(); SaveSettings(); }
        protected override void WndProc(ref Message m) { if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) ToggleOverlay(); base.WndProc(ref m); }
        protected override void OnFormClosing(FormClosingEventArgs e) { if (!_forceClose && _cfg.MinimizeToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); ShowInTaskbar = false; return; } _timer?.Stop(); try { UnregisterHotKey(this.Handle, 1); _ov?.Hide(); _tray.Visible = false; _tray.Dispose(); _comp?.Close(); _mbComp?.Close(); _mbComp?.Close(); } catch { } base.OnFormClosing(e); }

        private bool IsAuto() { try { using var p = Process.Start(new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }); p.WaitForExit(); return p.ExitCode == 0; } catch { return false; } }
        private void ToggleAuto() { string exe = Environment.ProcessPath ?? Application.ExecutablePath; using var p = Process.Start(new ProcessStartInfo("schtasks", !IsAuto() ? $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F" : $"/Delete /TN \"{TaskName}\" /F") { CreateNoWindow = true, UseShellExecute = false }); p.WaitForExit(); bool st = IsAuto(); _autoTop.Checked = st; _autoTray.Checked = st; }
        private void CopyReport()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== PC Minimal Monitor v1.1 (by SantaNyb) ===");
                sb.AppendLine($"CPU: {_cpuName} — {_cpuT:F0}°C | GPU: {_gpuName} — {_gpuT:F0}°C | RAM: {_ramU:F1}/{_ramTot:F1} ГБ");
                sb.AppendLine("\n--- ДИАГНОСТИКА ДАТЧИКОВ ПЛАТЫ (Статус: {_mbErr}) ---");
                foreach (var hw in _comp.Hardware.Concat(_mbComp?.Hardware ?? Enumerable.Empty<IHardware>()))
                {
                    sb.AppendLine($"[{hw.HardwareType}] {hw.Name}");
                    foreach (var s in hw.Sensors.Where(x => x.SensorType == SensorType.Fan || x.SensorType == SensorType.Control))
                        sb.AppendLine($"  - {s.SensorType}: {s.Name} = {s.Value}");
                    foreach (var sh in hw.SubHardware)
                    {
                        sb.AppendLine($"  Sub: {sh.Name}");
                        foreach (var s in sh.Sensors.Where(x => x.SensorType == SensorType.Fan || x.SensorType == SensorType.Control))
                            sb.AppendLine($"    * {s.SensorType}: {s.Name} = {s.Value}");
                    }
                }
                Clipboard.SetText(sb.ToString());
                if (_tray.Visible) _tray.ShowBalloonTip(2000, "Отчёт скопирован", "Сводка и диагностика датчиков скопированы.", ToolTipIcon.Info);
            }
            catch { }
        }

        private void UpdateSensors()
        {
            try
            {
                var drvs = new List<DriveItem>(); var fans = new List<FanItem>(); float? rTmp = null;
                foreach (var hw in _comp.Hardware.Concat(_mbComp?.Hardware ?? Enumerable.Empty<IHardware>()))
                {
                    hw.Update(); foreach (var sh in hw.SubHardware) sh.Update();
                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        if (!string.IsNullOrWhiteSpace(hw.Name)) _cpuName = hw.Name.Replace("AMD ", "").Replace("Intel(R) Core(TM) ", "").Replace("Intel ", "").ToUpper();
                        var ts = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
                        _cpuT = (ts.FirstOrDefault(s => s.Name.Contains("CCD1")) ?? ts.FirstOrDefault(s => s.Name.Contains("Tdie")) ?? ts.FirstOrDefault(s => s.Name.Contains("Package")) ?? ts.FirstOrDefault())?.Value ?? _cpuT;
                        if (_cpuT > _cpuMaxT) _cpuMaxT = _cpuT;
                        _cpuL = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total") && s.Value.HasValue)?.Value ?? _cpuL;
                        var clks = hw.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core") && s.Value > 0).Select(s => s.Value.Value).ToList();
                        if (clks.Count > 0) _cpuClock = clks.Max();
                        _cpuPower = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Contains("Package") && s.Value.HasValue)?.Value ?? _cpuPower;
                    }
                    else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel)
                    {
                        if (!string.IsNullOrWhiteSpace(hw.Name)) _gpuName = hw.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Replace("AMD ", "").Replace("Intel(R) ", "").ToUpper();
                        var ts = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToList();
                        _gpuT = (ts.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) ?? ts.FirstOrDefault(s => s.Name.Contains("Core") && !s.Name.Contains("Hot")) ?? ts.FirstOrDefault())?.Value ?? _gpuT;
                        if (_gpuT > _gpuMaxT) _gpuMaxT = _gpuT;
                        var ls = hw.Sensors.Where(s => s.SensorType == SensorType.Load && s.Value.HasValue).ToList();
                        _gpuL = (ls.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) ?? ls.FirstOrDefault())?.Value ?? _gpuL;
                        _gpuClock = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core") && s.Value.HasValue)?.Value ?? _gpuClock;
                        _gpuPower = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value.HasValue)?.Value ?? _gpuPower;
                        float vu = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Used") && s.Value.HasValue)?.Value ?? 0;
                        float vt = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name.Contains("Memory Total") && s.Value.HasValue)?.Value ?? 0;
                        if (vt > 0) { _vramUsed = vu / 1024f; _vramTotal = vt / 1024f; }
                    }
                    else if (hw.HardwareType == HardwareType.Memory) rTmp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0)?.Value;
                    else if (hw.HardwareType == HardwareType.Storage)
                    {
                        string dName = string.IsNullOrWhiteSpace(hw.Name) || hw.Name.Trim() == "..." ? "Накопитель (заблокировано изоляцией ядра)" : hw.Name;
                        var act = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Activity") && s.Value.HasValue);
                        var used = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Used") && s.Value.HasValue);
                        drvs.Add(new DriveItem { Name = dName, Temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value > 0)?.Value, Load = act?.Value ?? (used != null ? -used.Value : null) });
                    }
                    var all = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).Concat(hw.SubHardware.SelectMany(s => s.SubHardware).SelectMany(ss => ss.Sensors)).ToList();
                    var rpms = all.Where(s => s.SensorType == SensorType.Fan && s.Value.HasValue).ToList();
                    var ctrls = all.Where(s => s.SensorType == SensorType.Control && s.Value.HasValue).ToList();
                    for (int i = 0; i < rpms.Count; i++)
                    {
                        bool isGpu = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd;
                        if (!isGpu && rpms[i].Value.Value <= 0 && !rpms[i].Name.Contains("CPU") && !rpms[i].Name.Contains("Pump")) continue;
                        float? pct = ctrls.FirstOrDefault(c => c.Name == rpms[i].Name)?.Value ?? (i < ctrls.Count ? ctrls[i].Value : null);
                        fans.Add(new FanItem { Name = (isGpu ? "GPU: " : "") + rpms[i].Name, Rpm = rpms[i].Value.Value, Pct = pct });
                    }
                }
                var ms = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(ms)) { _ramTot = ms.ullTotalPhys / 1073741824f; _ramU = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824f; _ramPct = ms.dwMemoryLoad; }
                if (drvs.Count == 0)
                {
                    foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
                    {
                        float usedPct = (float)((d.TotalSize - d.TotalFreeSpace) * 100.0 / d.TotalSize);
                        string lbl = string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"Локальный диск ({d.Name.TrimEnd('\\')})" : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
                        drvs.Add(new DriveItem { Name = lbl, Temp = null, Load = -usedPct });
                    }
                }
                _ramT = rTmp; _drives = drvs; _fans = fans;

                long rx = 0, tx = 0;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)) { var st = ni.GetIPv4Statistics(); rx += st.BytesReceived; tx += st.BytesSent; }
                var now = DateTime.UtcNow; double sec = (now - _prevNetCheck).TotalSeconds;
                if (_prevNetCheck != DateTime.MinValue && sec > 0.2 && _prevBytesRx > 0) { _netDownMb = Math.Max(0, (rx - _prevBytesRx) / 1048576.0 / sec); _netUpMb = Math.Max(0, (tx - _prevBytesTx) / 1048576.0 / sec); }
                _prevBytesRx = rx; _prevBytesTx = tx; _prevNetCheck = now;

                double el = (now - _prevCheck).TotalMilliseconds;
                if (el >= 300)
                {
                    string mCpu = "—", mRam = "—"; double maxC = 0; long maxM = 0; var nextT = new Dictionary<int, TimeSpan>();
                    foreach (var p in Process.GetProcesses()) { try { if (p.Id <= 4) continue; if (p.WorkingSet64 > maxM) { maxM = p.WorkingSet64; mRam = p.ProcessName; } var t = p.TotalProcessorTime; nextT[p.Id] = t; if (_prevProc.TryGetValue(p.Id, out var pt)) { double pct = ((t - pt).TotalMilliseconds / (el * Environment.ProcessorCount)) * 100; if (pct > maxC) { maxC = pct; mCpu = p.ProcessName; } } } catch { } finally { p.Dispose(); } }
                    _prevProc = nextT; _prevCheck = now;
                    _topCpu = maxC > 0.5 ? $"{mCpu} ({maxC:F0}%)" : "Простой";
                    _topRam = maxM > 0 ? $"{mRam} ({maxM / 1073741824.0:F1} ГБ)" : "—";
                }

                if (_cfg.OverheatAlert && (_cpuT >= 85 || _gpuT >= 85) && (now - _lastAlert).TotalMinutes >= 1) { _lastAlert = now; if (_tray.Visible) _tray.ShowBalloonTip(4000, "Внимание: высокая температура!", $"CPU: {_cpuT:F0}°C | GPU: {_gpuT:F0}°C", ToolTipIcon.Warning); }
                _cpuHist.Add(_cpuT); if (_cpuHist.Count > MaxPts) _cpuHist.RemoveAt(0);
                _gpuHist.Add(_gpuT); if (_gpuHist.Count > MaxPts) _gpuHist.RemoveAt(0);
                if (_tray.Visible) { string tip = $"CPU: {_cpuT:F0}°C ({_cpuL:F0}%) | GPU: {_gpuT:F0}°C ({_gpuL:F0}%)"; _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; }
                if (_cfg.OvEnabled) UpdateOverlay();
                if (Visible) Invalidate();
            }
            catch { }
        }

        private void UpdateOverlay()
        {
            var parts = new List<string> { $"CPU {(_cpuT > 0 ? $"{_cpuT:F0}°C" : "Н/Д")} ({_cpuL:F0}%)", $"GPU {_gpuT:F0}°C ({_gpuL:F0}%)", $"RAM {_ramPct:F0}%" };
            if (_cfg.OvShowVram && _vramTotal > 0) parts.Add($"VRAM {_vramUsed:F1}ГБ");
            if (_cfg.OvShowTime) parts.Add(DateTime.Now.ToString("HH:mm"));
            _ov.UpdateContent(string.Join(_cfg.OvVertical ? "\n" : "  |  ", parts), _cfg);
        }

        private static void DrawIcon(Graphics g, int type, int x, int y, Color col)
        {
            using var p = new Pen(col, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; using var b = new SolidBrush(col);
            if (type == 0) { g.DrawLines(p, new[] { new Point(x + 1, y + 7), new Point(x + 7, y + 1), new Point(x + 13, y + 7) }); g.DrawRectangle(p, x + 3, y + 6, 8, 7); }
            else if (type == 1) { g.DrawRectangle(p, x + 1, y + 3, 12, 8); g.FillEllipse(b, x + 9, y + 6, 2, 2); }
            else if (type == 2) { g.DrawEllipse(p, x + 1, y + 1, 12, 12); g.FillEllipse(b, x + 5.5f, y + 5.5f, 3, 3); for (int a = 0; a < 3; a++) { double r = a * 2.094; g.DrawLine(p, x + 7 + (float)Math.Cos(r) * 2.5f, y + 7 + (float)Math.Sin(r) * 2.5f, x + 7 + (float)Math.Cos(r + 0.7) * 5.2f, y + 7 + (float)Math.Sin(r + 0.7) * 5.2f); } }
            else if (type == 3) { g.DrawRectangle(p, x + 3, y + 3, 8, 8); g.DrawLine(p, x + 5, y + 1, x + 5, y + 3); g.DrawLine(p, x + 9, y + 1, x + 9, y + 3); g.DrawLine(p, x + 5, y + 11, x + 5, y + 13); g.DrawLine(p, x + 9, y + 11, x + 9, y + 13); g.DrawLine(p, x + 1, y + 5, x + 3, y + 5); g.DrawLine(p, x + 1, y + 9, x + 3, y + 9); g.DrawLine(p, x + 11, y + 5, x + 13, y + 5); g.DrawLine(p, x + 11, y + 9, x + 13, y + 9); }
            else if (type == 4) { g.DrawRectangle(p, x + 1, y + 3, 12, 8); g.DrawEllipse(p, x + 3, y + 5, 4, 4); g.DrawEllipse(p, x + 8, y + 5, 4, 4); }
            else if (type == 5) { g.DrawRectangle(p, x + 1, y + 3, 12, 7); g.FillRectangle(b, x + 3, y + 5, 2, 3); g.FillRectangle(b, x + 6, y + 5, 2, 3); g.FillRectangle(b, x + 9, y + 5, 2, 3); g.DrawLine(p, x + 3, y + 12, x + 5, y + 12); g.DrawLine(p, x + 8, y + 12, x + 11, y + 12); }
            else if (type == 6) { g.DrawEllipse(p, x + 1, y + 1, 12, 12); g.DrawLine(p, x + 7, y + 3, x + 7, y + 7); g.DrawLine(p, x + 7, y + 7, x + 10, y + 9); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(_cfg.UiScale, _cfg.UiScale);
            var thm = GetTheme();
            if (_tab == 0)
            {
                string cSub = _cpuT <= 0 && _cpuClock <= 0 ? "Заблокировано изоляцией ядра Windows" : $"{(_cpuClock > 0 ? $"{_cpuClock:F0} МГц  •  " : "")}{(_cpuPower > 0 ? $"{_cpuPower:F0} Вт" : "Активен")}";
                string gSub = $"{(_gpuClock > 0 ? $"{_gpuClock:F0} МГц  •  " : "")}{(_gpuPower > 0 ? $"{_gpuPower:F0} Вт  •  " : "")}{(_vramTotal > 0 ? $"VRAM: {_vramUsed:F1}/{_vramTotal:F0} ГБ" : "")}";
                DrawMainBlock(g, 12 + TopOff, 3, _cpuName, cSub, _cpuT, _cpuMaxT, _cpuL, _cpuHist, GetTempCol(_cpuT, thm.Cpu), _topCpu);
                DrawMainBlock(g, 208 + TopOff, 4, _gpuName, gSub, _gpuT, _gpuMaxT, _gpuL, _gpuHist, GetTempCol(_gpuT, thm.Gpu), null);
                DrawRamBlock(g, 404 + TopOff, thm.Ram);
            }
            else if (_tab == 1) DrawListTab(g, 1, "НАКОПИТЕЛИ (SSD / NVMe M.2)", _drives.Select(d => (d.Name, d.Temp.HasValue ? $"{d.Temp:F0}°C" : "Н/Д", d.Load.HasValue ? (d.Load >= 0 ? $"Нагрузка: {d.Load:F0}%" : $"Занято: {-d.Load:F0}%") : "Активен", thm.Cpu)).ToList(), "Накопители с датчиками не найдены");
            
            else DrawSystemTab(g, thm.Cpu);
        }

        private void DrawMainBlock(Graphics g, int y, int iconType, string title, string sub, float temp, float maxT, float load, List<float> hist, Color col, string topProc)
        {
            using var fT = new Font("Segoe UI", 10f, FontStyle.Bold); using var fSub = new Font("Segoe UI", 8f); using var fL = new Font("Segoe UI", 8.5f); using var fB = new Font("Segoe UI", 22, FontStyle.Bold);
            using var bG = new SolidBrush(Color.FromArgb(160, 160, 175)); using var bC = new SolidBrush(col); using var bg = new SolidBrush(Color.FromArgb(30, 30, 36)); using var pB = new Pen(Color.FromArgb(55, 55, 65), 1);

            DrawIcon(g, iconType, 20, y + 2, col);
            string shortTitle = title.Length > 24 ? title.Substring(0, 24) + "…" : title;
            g.DrawString(shortTitle, fT, Brushes.White, 40, y);
            float titleW = g.MeasureString(shortTitle, fT).Width;
            if (!string.IsNullOrEmpty(sub)) g.DrawString(sub, fSub, bG, 46 + titleW, y + 2);
            g.DrawString(temp > 0 ? $"{temp:F0}°C" : "Н/Д", fT, bC, 455, y);
            if (maxT > 0) g.DrawString($"Макс: {maxT:F0}°C", fSub, bG, 515, y + 2);

            var gR = new Rectangle(20, y + 26, 420, 132); g.FillRectangle(bg, gR); g.DrawRectangle(pB, gR);
            using var gP = new Pen(Color.FromArgb(45, 45, 54), 1) { DashStyle = DashStyle.Dash }; int y50 = gR.Bottom - gR.Height / 2; g.DrawLine(gP, gR.Left, y50, gR.Right, y50); g.DrawString("50°", new Font("Segoe UI", 7), bG, gR.Left + 4, y50 - 13);
            if (hist.Count >= 2) { using var p = new Pen(col, 2.5f); var pts = new PointF[hist.Count]; float sx = (float)gR.Width / (MaxPts - 1); for (int i = 0; i < hist.Count; i++) pts[i] = new PointF(gR.Left + i * sx, gR.Bottom - (Math.Clamp(hist[i], 0, 100) / 100f) * gR.Height); g.DrawCurve(p, pts, 0.3f); }

            var lR = new Rectangle(455, y + 26, 155, 132); g.FillRectangle(bg, lR); g.DrawRectangle(pB, lR);
            g.DrawString("НАГРУЗКА", fL, bG, lR.Left + 14, lR.Top + 10); g.DrawString($"{load:F0}%", fB, Brushes.White, lR.Left + 12, lR.Top + 30);
            if (!string.IsNullOrEmpty(topProc)) g.DrawString($"Топ: {(topProc.Length > 21 ? topProc.Substring(0, 21) + "…" : topProc)}", fSub, bG, lR.Left + 14, lR.Top + 76);
            var bar = new Rectangle(lR.Left + 14, lR.Bottom - 22, lR.Width - 28, 8); g.FillRectangle(new SolidBrush(Color.FromArgb(45, 45, 54)), bar);
            int w = (int)((Math.Clamp(load, 0, 100) / 100f) * bar.Width); if (w > 0) g.FillRectangle(bC, new Rectangle(bar.Left, bar.Top, w, bar.Height));
        }
        private void DrawRamBlock(Graphics g, int y, Color col)
        {
            using var fT = new Font("Segoe UI", 10f, FontStyle.Bold); using var fS = new Font("Segoe UI", 8.5f); using var bG = new SolidBrush(Color.FromArgb(160, 160, 175)); using var bP = new SolidBrush(col);
            var r = new Rectangle(20, y, 590, 80); g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 36)), r); g.DrawRectangle(new Pen(Color.FromArgb(55, 55, 65), 1), r);
            DrawIcon(g, 5, r.Left + 14, r.Top + 13, col);
            g.DrawString($"ОПЕРАТИВНАЯ ПАМЯТЬ (ОЗУ){(_ramT.HasValue ? $"   •   Температура: {_ramT:F0}°C" : "")}", fT, Brushes.White, r.Left + 34, r.Top + 12);
            g.DrawString($"{_ramU:F1} / {_ramTot:F1} ГБ ({_ramPct:F0}%)", fT, bP, r.Right - 185, r.Top + 12);
            g.DrawString($"Самый прожорливый процесс: {(_topRam.Length > 32 ? _topRam.Substring(0, 32) + "…" : _topRam)}", fS, bG, r.Left + 15, r.Top + 36);
            var bar = new Rectangle(r.Left + 15, r.Bottom - 18, r.Width - 30, 8); g.FillRectangle(new SolidBrush(Color.FromArgb(45, 45, 54)), bar);
            int w = (int)((Math.Clamp(_ramPct, 0, 100) / 100f) * bar.Width); if (w > 0) g.FillRectangle(bP, new Rectangle(bar.Left, bar.Top, w, bar.Height));
        }

        private void DrawListTab(Graphics g, int iconType, string header, List<(string Name, string Val1, string Val2, Color Col)> items, string emptyMsg)
        {
            using var fH = new Font("Segoe UI", 11f, FontStyle.Bold); using var fR = new Font("Segoe UI", 10f, FontStyle.Bold); using var fS = new Font("Segoe UI", 9.5f); using var bG = new SolidBrush(Color.FromArgb(160, 160, 175));
            int y = 18 + TopOff; DrawIcon(g, iconType, 20, y + 3, GetTheme().Cpu); g.DrawString(header, fH, Brushes.White, 40, y); y += 34;
            if (items.Count == 0) { g.DrawString(emptyMsg, fS, bG, 20, y); return; }
            foreach (var it in items.Take(8)) { var r = new Rectangle(20, y, 590, 44); g.FillRectangle(new SolidBrush(Color.FromArgb(30, 30, 36)), r); g.DrawRectangle(new Pen(Color.FromArgb(55, 55, 65), 1), r); g.DrawString(it.Name.Length > 36 ? it.Name.Substring(0, 36) + "..." : it.Name, fR, Brushes.White, r.Left + 15, r.Top + 12); g.DrawString(it.Val1, fR, new SolidBrush(it.Col), r.Right - 245, r.Top + 12); g.DrawString(it.Val2, fS, bG, r.Right - 95, r.Top + 13); y += 52; }
        }

        private void DrawSystemTab(Graphics g, Color col)
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64); var pwr = System.Windows.Forms.SystemInformation.PowerStatus;
            string bat = pwr.BatteryChargeStatus == BatteryChargeStatus.NoSystemBattery ? "От сети (ПК)" : $"{pwr.BatteryLifePercent * 100:F0}% ({(pwr.PowerLineStatus == PowerLineStatus.Online ? "Зарядка" : "АКБ")})";
            DrawListTab(g, 6, "СИСТЕМА И СЕТЬ", new List<(string, string, string, Color)> { ("Скорость интернета (Скачивание)", $"↓ {_netDownMb:F2} МБ/с", $"{_netDownMb * 8:F1} Мбит/с", col), ("Скорость интернета (Отдача)", $"↑ {_netUpMb:F2} МБ/с", $"{_netUpMb * 8:F1} Мбит/с", GetTheme().Gpu), ("Время работы ПК без перезагрузки", $"{(int)up.TotalHours} ч {up.Minutes} мин", "Аптайм", Color.White), ("Питание и аккумулятор", bat, "Статус", Color.FromArgb(180, 180, 195)) }, "");
        }
    }
    public class OverlayForm : Form
    {
        private string _text = "";
        private AppSettings _cfg;
        private Action _onSave;
        private bool _drag;
        private Point _dragStart;

        public OverlayForm(AppSettings cfg, Action onSave)
        {
            _cfg = cfg; _onSave = onSave;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(1, 1, 1);
            TransparencyKey = Color.FromArgb(1, 1, 1);
            MouseDown += (s, e) => { if (_cfg.OvCorner == 4 && e.Button == MouseButtons.Left) { _drag = true; _dragStart = e.Location; } };
            MouseMove += (s, e) => { if (_drag) Location = new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y); };
            MouseUp += (s, e) => { if (_drag) { _drag = false; _cfg.OvCustomX = Left; _cfg.OvCustomY = Top; _onSave(); } };
            ApplyStyle(cfg);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000; if (_cfg?.OvCorner != 4) cp.ExStyle |= 0x20; return cp; }
        }

        public void ApplyStyle(AppSettings cfg)
        {
            _cfg = cfg;
            UpdateStyles();
            ResizeToText();
            Invalidate();
        }

        private void ResizeToText()
        {
            if (string.IsNullOrEmpty(_text)) return;
            using var g = CreateGraphics();
            using var f = new Font("Consolas", _cfg.OvFontSize, FontStyle.Bold);
            var sz = g.MeasureString(_text, f);
            Size = new Size((int)Math.Ceiling(sz.Width) + 22, (int)Math.Ceiling(sz.Height) + 14);
        }

        public void UpdateContent(string text, AppSettings cfg)
        {
            _cfg = cfg;
            if (_text != text) { _text = text; ResizeToText(); }
            var s = Screen.PrimaryScreen.WorkingArea;
            Location = cfg.OvCorner switch
            {
                1 => new Point(s.Right - Width - 16, s.Top + 16),
                2 => new Point(s.Left + 16, s.Bottom - Height - 16),
                3 => new Point(s.Right - Width - 16, s.Bottom - Height - 16),
                4 => new Point(cfg.OvCustomX, cfg.OvCustomY),
                _ => new Point(s.Left + 16, s.Top + 16)
            };
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (string.IsNullOrEmpty(_text)) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (_cfg.OvBgMode == 0)
            {
                using var bg = new SolidBrush(Color.FromArgb(150, 18, 18, 24));
                g.FillRectangle(bg, 0, 0, Width, Height);
            }
            else if (_cfg.OvBgMode == 1)
            {
                using var bg = new SolidBrush(Color.FromArgb(255, 14, 14, 18));
                g.FillRectangle(bg, 0, 0, Width, Height);
            }

            var txtCol = Color.FromArgb(_cfg.OvColorArgb);
            bool isDarkText = txtCol.R < 40 && txtCol.G < 40 && txtCol.B < 40;
            var outlineCol = isDarkText ? Color.FromArgb(245, 245, 250) : Color.FromArgb(10, 10, 14);

            using var path = new GraphicsPath();
            using var ff = new FontFamily("Consolas");
            float emSize = g.DpiY * _cfg.OvFontSize / 72f;
            string[] lines = _text.Split('\n');
            float y = 7f;
            foreach (var line in lines)
            {
                path.AddString(line, ff, (int)FontStyle.Bold, emSize, new PointF(10f, y), StringFormat.GenericDefault);
                y += emSize * 1.25f;
            }

            using var outlinePen = new Pen(outlineCol, 2.4f) { LineJoin = LineJoin.Round };
            using var fillBrush = new SolidBrush(txtCol);
            g.DrawPath(outlinePen, path);
            g.FillPath(fillBrush, path);
        }
    }

    public class DarkRenderer : ToolStripProfessionalRenderer { public DarkRenderer() : base(new DarkTable()) { } protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Color.FromArgb(235, 235, 245); base.OnRenderItemText(e); } }
    public class DarkTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(52, 54, 66); public override Color MenuItemBorder => Color.FromArgb(75, 78, 92); public override Color MenuBorder => Color.FromArgb(60, 60, 70);
        public override Color ToolStripDropDownBackground => Color.FromArgb(28, 28, 34); public override Color ImageMarginGradientBegin => Color.FromArgb(28, 28, 34); public override Color ImageMarginGradientMiddle => Color.FromArgb(28, 28, 34); public override Color ImageMarginGradientEnd => Color.FromArgb(28, 28, 34);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(52, 54, 66); public override Color MenuItemSelectedGradientEnd => Color.FromArgb(52, 54, 66);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(42, 44, 54); public override Color MenuItemPressedGradientMiddle => Color.FromArgb(42, 44, 54); public override Color MenuItemPressedGradientEnd => Color.FromArgb(42, 44, 54);
        public override Color CheckBackground => Color.FromArgb(0, 140, 210); public override Color CheckSelectedBackground => Color.FromArgb(0, 160, 235);
    }
}










