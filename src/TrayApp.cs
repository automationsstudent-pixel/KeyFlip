// Tray icon, menu, settings reload, and program entry point.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyFlip
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\KeyFlip_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("KeyFlip is already running - look for its icon near the clock.", "KeyFlip",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        readonly NotifyIcon tray;
        readonly Engine engine;
        readonly Control ui = new Control();
        readonly Icon iconOn, iconOff, iconFlash;
        readonly System.Windows.Forms.Timer flashTimer = new System.Windows.Forms.Timer { Interval = 900 };
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
        FileSystemWatcher watcher;
        ToolStripMenuItem miEnabled, miAuto, miLog, miStartup, miStatus, miSites;
        string pausedRule;  // set while typing in a window that matches an "off here" site
        int fixesToday; DateTime day = DateTime.Today;
        string lastFix = "";

        public TrayApp()
        {
            var forceHandle = ui.Handle; // needed so worker-thread events can be marshalled to the UI thread
            iconOn = LoadIcon("on.ico"); iconOff = LoadIcon("off.ico"); iconFlash = LoadIcon("flash.ico");

            var cfg = Config.Load();
            Lexicon lex;
            using (var en = Resource("en.txt.gz")) using (var he = Resource("he.txt.gz")) lex = Lexicon.Load(en, he);
            engine = new Engine(lex, cfg);
            engine.ApplyConfig(cfg, Config.LoadWords());
            engine.Fixed += (plan, app) => { try { ui.BeginInvoke((Action)(() => OnFixed(plan))); } catch { } };
            engine.SitePausedChanged += rule => { try { ui.BeginInvoke((Action)(() => { pausedRule = rule; UpdateIcon(); })); } catch { } };

            tray = new NotifyIcon { Visible = true, ContextMenuStrip = BuildMenu() };
            tray.DoubleClick += (s, e) => ToggleEnabled();
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); UpdateIcon(); };
            reloadTimer.Tick += (s, e) => { reloadTimer.Stop(); Reload(); };
            UpdateIcon();

            engine.Start();
            WatchConfig();

            if (engine.LastError != null)
                tray.ShowBalloonTip(8000, "KeyFlip could not start", engine.LastError, ToolTipIcon.Error);
            else if (!Engine.HasBothLayouts())
                tray.ShowBalloonTip(8000, "KeyFlip needs Hebrew and English",
                    "Add both keyboards in Windows Settings > Time & language > Language & region.", ToolTipIcon.Warning);
            else if (!cfg.ShownWelcome)
            {
                tray.ShowBalloonTip(10000, "KeyFlip is running",
                    "Type in either language - words typed in the wrong keyboard are fixed when you press space.\n" +
                    HotkeyText() + " undoes a fix or flips what you just typed.", ToolTipIcon.Info);
                cfg.ShownWelcome = true; cfg.Save();
            }
        }

        static Stream Resource(string name)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeyFlip." + name);
            return name.EndsWith(".gz") ? (Stream)new GZipStream(s, CompressionMode.Decompress) : s;
        }

        static Icon LoadIcon(string name)
        {
            using (var s = Resource(name)) return new Icon(s, SystemInformation.SmallIconSize);
        }

        string HotkeyText()
        {
            var c = engine.Cfg;
            return c.Hotkey + (c.Hotkey2 != null ? " (or " + c.Hotkey2 + ")" : "");
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            miStatus = new ToolStripMenuItem("KeyFlip") { Enabled = false };
            miEnabled = new ToolStripMenuItem("On", null, (s, e) => ToggleEnabled()) { CheckOnClick = false };
            miAuto = new ToolStripMenuItem("Fix automatically as I type", null, (s, e) =>
            {
                engine.Cfg.Core.AutoFix = !engine.Cfg.Core.AutoFix; engine.Cfg.Save(); UpdateIcon();
            });
            miLog = new ToolStripMenuItem("Keep a log of fixes (for tuning)", null, (s, e) =>
            {
                engine.Cfg.LogFixes = !engine.Cfg.LogFixes; engine.Cfg.Save(); UpdateIcon();
            });
            miStartup = new ToolStripMenuItem("Start with Windows", null, (s, e) => ToggleStartup());
            miSites = new ToolStripMenuItem("Sites where it's off");

            m.Items.Add(miStatus);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(miEnabled);
            m.Items.Add(miAuto);
            m.Items.Add("Turn off on this site / window...", null, (s, e) => AddSiteDialog());
            m.Items.Add(miSites);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("My words (never change these)...", null, (s, e) => OpenFile(Config.WordsPath));
            m.Items.Add("Settings file...", null, (s, e) => OpenFile(Config.SettingsPath));
            m.Items.Add(miLog);
            m.Items.Add("Open fix log", null, (s, e) => { if (File.Exists(Config.LogPath)) OpenFile(Config.LogPath); });
            m.Items.Add(miStartup);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("How to use", null, (s, e) => ShowHelp());
            m.Items.Add("Exit", null, (s, e) => ExitThread());
            m.Opening += (s, e) => { UpdateIcon(); BuildSitesMenu(); };
            return m;
        }

        void ToggleEnabled() { engine.Enabled = !engine.Enabled; engine.Cfg.Save(); UpdateIcon(); }

        void UpdateIcon()
        {
            if (day != DateTime.Today) { day = DateTime.Today; fixesToday = 0; }
            bool on = engine.Cfg.Enabled;
            bool paused = on && pausedRule != null;
            if (!flashTimer.Enabled) tray.Icon = on && !paused ? iconOn : iconOff;
            string status = !on ? "Off" : paused ? "Off on this site (" + pausedRule + ")"
                          : engine.Cfg.Core.AutoFix ? "On - fixing automatically" : "On - hotkey only";
            string text = "KeyFlip: " + status + "\n" + fixesToday + " fixes today" + (lastFix.Length > 0 ? "\nLast: " + lastFix : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            miStatus.Text = "KeyFlip - " + status + " (" + fixesToday + " fixes today)";
            miEnabled.Checked = on;
            miAuto.Checked = engine.Cfg.Core.AutoFix;
            miAuto.Enabled = on;
            miLog.Checked = engine.Cfg.LogFixes;
            miStartup.Checked = IsStartupEnabled();
        }

        void BuildSitesMenu()
        {
            miSites.DropDownItems.Clear();
            var sites = engine.Cfg.ExcludedSites;
            miSites.Text = "Sites where it's off (" + sites.Count + ")";
            foreach (var site in sites.ToArray())
            {
                string rule = site;
                miSites.DropDownItems.Add("Turn back on for:  " + rule, null, (s, e) =>
                {
                    lock (engine.Sync) engine.Cfg.ExcludedSites.RemoveAll(x => x.Equals(rule, StringComparison.OrdinalIgnoreCase));
                    engine.Cfg.Save(); // the settings watcher reloads it into the engine
                    tray.ShowBalloonTip(4000, "KeyFlip", "Back on for windows containing \"" + rule + "\".", ToolTipIcon.Info);
                });
            }
            if (sites.Count > 0) miSites.DropDownItems.Add(new ToolStripSeparator());
            miSites.DropDownItems.Add("Edit the list in the settings file...", null, (s, e) => OpenFile(Config.SettingsPath));
        }

        static readonly Regex BrowserSuffix = new Regex(
            @"\s+[-–—|]\s+(Google Chrome|Microsoft\W*Edge|Mozilla Firefox|Firefox|Opera|Brave)\s*$", RegexOptions.IgnoreCase);
        static readonly Regex ProfileName = new Regex(@"^(Personal|Work|Default|Profile \d+|אישי|עבודה|InPrivate)$", RegexOptions.IgnoreCase);
        static readonly Regex MorePages = new Regex(@"\s+(and \d+ more pages?|ועוד \d+ (דפים|דף))", RegexOptions.IgnoreCase);

        static string CleanTitle(string t)
        {
            t = (t ?? "").Replace("\u200b", "");
            t = BrowserSuffix.Replace(t, "");
            t = MorePages.Replace(t, "");
            return t.Trim();
        }

        void AddSiteDialog()
        {
            IntPtr w = engine.LastTypedWindow;
            string fullTitle = w == IntPtr.Zero ? "" : Engine.WindowTitle(w);
            string cleaned = CleanTitle(fullTitle);
            // Browser tabs often look like "Item 123 - SiteName": offer each part, last first (it usually stays the same).
            // Skip Edge/Chrome profile names ("Personal", "Work") that sit between the page and the browser name.
            var choices = new List<string>();
            var parts = Regex.Split(cleaned, @"\s+[-–—|]\s+");
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string part = parts[i].Trim();
                if (part.Length >= 2 && !ProfileName.IsMatch(part) && !choices.Contains(part)) choices.Add(part);
            }
            if (cleaned.Length > 0 && !choices.Contains(cleaned)) choices.Add(cleaned);
            string suggestion = choices.Count > 0 ? choices[0] : "";

            using (var f = new Form())
            {
                f.Text = "KeyFlip - turn off on a site";
                f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.MinimizeBox = false;
                f.StartPosition = FormStartPosition.CenterScreen; f.TopMost = true; f.ShowInTaskbar = false;
                f.AutoScaleMode = AutoScaleMode.Font; f.Font = SystemFonts.MessageBoxFont;
                f.ClientSize = new Size(560, 210); f.Padding = new Padding(12);

                var intro = new Label
                {
                    AutoSize = false, Location = new Point(12, 12), Size = new Size(536, 52),
                    Text = "KeyFlip will stay off in any window or browser tab whose title contains this text. " +
                           "Pick or type the part that stays the same on every page of the site - usually the site's name."
                };
                var box = new ComboBox { Location = new Point(12, 70), Size = new Size(536, 26), DropDownStyle = ComboBoxStyle.DropDown };
                box.Items.AddRange(choices.ToArray());
                box.Text = suggestion;
                var seen = new Label
                {
                    AutoSize = false, Location = new Point(12, 104), Size = new Size(536, 40), ForeColor = SystemColors.GrayText,
                    Text = fullTitle.Length > 0 ? "Title of the window you last typed in:\n" + fullTitle
                                                : "Tip: click into the site and type something first, then open this again."
                };
                var ok = new Button { Text = "Turn off here", DialogResult = DialogResult.OK, Location = new Point(352, 160), Size = new Size(110, 32) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(468, 160), Size = new Size(80, 32) };
                f.Controls.AddRange(new Control[] { intro, box, seen, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => { f.Activate(); box.Focus(); box.SelectAll(); };

                if (f.ShowDialog() != DialogResult.OK) return;
                string rule = box.Text.Replace(";", " ").Trim();
                if (rule.Length < 2) return;
                if (!engine.Cfg.ExcludedSites.Any(x => x.Equals(rule, StringComparison.OrdinalIgnoreCase)))
                {
                    lock (engine.Sync) engine.Cfg.ExcludedSites.Add(rule);
                    engine.Cfg.Save(); // the settings watcher reloads it into the engine
                }
                tray.ShowBalloonTip(5000, "KeyFlip", "Off in windows containing \"" + rule + "\". " +
                    "To turn it back on: tray menu > Sites where it's off.", ToolTipIcon.Info);
            }
        }

        void OnFixed(FixPlan plan)
        {
            if (plan.Kind != "undo") fixesToday++;
            string a = plan.Before ?? "", b = plan.After ?? "";
            if (a.Length > 18) a = a.Substring(0, 18) + "…";
            if (b.Length > 18) b = b.Substring(0, 18) + "…";
            lastFix = a + " → " + b;
            tray.Icon = iconFlash;
            flashTimer.Stop(); flashTimer.Start();
            UpdateIcon();
        }

        void WatchConfig()
        {
            try
            {
                watcher = new FileSystemWatcher(Config.Dir) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                watcher.Changed += (s, e) => ScheduleReload(e.Name);
                watcher.Created += (s, e) => ScheduleReload(e.Name);
                watcher.Renamed += (s, e) => ScheduleReload(e.Name);
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        void ScheduleReload(string name)
        {
            if (name == null) return;
            if (!name.Equals("settings.ini", StringComparison.OrdinalIgnoreCase) && !name.Equals("my-words.txt", StringComparison.OrdinalIgnoreCase)) return;
            try { ui.BeginInvoke((Action)(() => { reloadTimer.Stop(); reloadTimer.Start(); })); } catch { }
        }

        void Reload()
        {
            engine.ApplyConfig(Config.Load(), Config.LoadWords());
            UpdateIcon();
        }

        static void OpenFile(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
        }

        static bool IsStartupEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("KeyFlip") != null; }
            catch { return false; }
        }

        void ToggleStartup()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (IsStartupEnabled()) k.DeleteValue("KeyFlip", false);
                    else k.SetValue("KeyFlip", "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception ex) { MessageBox.Show("Could not change the startup setting: " + ex.Message, "KeyFlip"); }
            UpdateIcon();
        }

        void ShowHelp()
        {
            MessageBox.Show(
                "KeyFlip fixes text typed in the wrong keyboard language (Hebrew / English).\n\n" +
                "AUTOMATIC\nJust type. When you press space, Enter or punctuation, a word typed in the wrong keyboard " +
                "is replaced (akuo → שלום, יקךךם → hello) and the keyboard switches for you. Earlier words of the same " +
                "sentence are fixed too once it's clear which language you meant.\n\n" +
                "HOTKEY: " + HotkeyText() + "\n" +
                "• Right after an automatic fix: undoes it, and remembers the word so it isn't changed again.\n" +
                "• Otherwise: flips everything you typed since you last clicked (useful if auto-fix missed it).\n" +
                "• Shift + hotkey, or the hotkey with text selected and nothing typed: flips the selected text.\n\n" +
                "MY WORDS\nAdd company terms, codes and names that should never be changed (tray menu → My words).\n\n" +
                "PRIVACY\nEverything stays on this computer. KeyFlip has no network access, and it only remembers the " +
                "current sentence in memory. Password fields and apps listed in the settings file are skipped.\n\n" +
                "Double-click the tray icon to turn KeyFlip on or off.",
                "How to use KeyFlip", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void ExitThreadCore()
        {
            engine.Dispose();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
