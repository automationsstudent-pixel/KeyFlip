// Settings file, personal word list and the optional fix log, all under %APPDATA%\KeyFlip.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace KeyFlip
{
    public class Hotkey
    {
        public Keys Key; public bool Ctrl, Alt, Shift, Win;

        public static Hotkey Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var hk = new Hotkey();
            foreach (var raw in s.Split('+'))
            {
                var p = raw.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") hk.Ctrl = true;
                else if (p == "alt") hk.Alt = true;
                else if (p == "shift") hk.Shift = true;
                else if (p == "win") hk.Win = true;
                else
                {
                    Keys k;
                    if (p == "pause" || p == "break") k = Keys.Pause;
                    else if (!Enum.TryParse(raw.Trim(), true, out k)) return null;
                    hk.Key = k;
                }
            }
            return hk.Key == Keys.None ? null : hk;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl"); if (Alt) parts.Add("Alt"); if (Shift) parts.Add("Shift"); if (Win) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }

    public class Config
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyFlip");
        public static string SettingsPath { get { return Path.Combine(Dir, "settings.ini"); } }
        public static string WordsPath { get { return Path.Combine(Dir, "my-words.txt"); } }
        public static string LogPath { get { return Path.Combine(Dir, "fix-log.txt"); } }

        public Settings Core = new Settings();
        public bool Enabled = true;
        public bool LogFixes = false;
        public bool ShownWelcome = false;
        public Hotkey Hotkey = Hotkey.Parse("Pause");
        public Hotkey Hotkey2 = Hotkey.Parse("Ctrl+Alt+Space");
        public List<string> ExcludedSites = new List<string>(); // window-title text, e.g. a website's name
        public HashSet<string> ExcludedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mstsc.exe", "vmconnect.exe", "wfica32.exe", "CDViewer.exe", "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe" };

        public static Config Load()
        {
            var c = new Config();
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(SettingsPath)) { c.Save(); }
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    string k = t.Substring(0, eq).Trim().ToLowerInvariant(), v = t.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "enabled": c.Enabled = Bool(v, true); break;
                        case "autofix": c.Core.AutoFix = Bool(v, true); break;
                        case "lookback": c.Core.Lookback = Bool(v, true); break;
                        case "logfixes": c.LogFixes = Bool(v, false); break;
                        case "shownwelcome": c.ShownWelcome = Bool(v, false); break;
                        case "hotkey": c.Hotkey = Hotkey.Parse(v) ?? c.Hotkey; break;
                        case "hotkey2": c.Hotkey2 = Hotkey.Parse(v); break;
                        case "minwordlength": c.Core.MinWordLength = Int(v, 2); break;
                        case "minscore": c.Core.MinScore = Dbl(v, 1.0); break;
                        case "minscoreshort": c.Core.MinScoreShort = Dbl(v, 20); break;
                        case "ratio": c.Core.Ratio = Dbl(v, 20); break;
                        case "keepiftypedabove": c.Core.KeepIfTypedAbove = Dbl(v, 10); break;
                        case "excludedsites":
                            c.ExcludedSites = v.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                            break;
                        case "excludedapps":
                            c.ExcludedApps = new HashSet<string>(v.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
                            break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine("# KeyFlip settings. Save this file and KeyFlip reloads it automatically.");
                sb.AppendLine("Enabled=" + Enabled);
                sb.AppendLine("# Fix words automatically when you press space/enter/punctuation");
                sb.AppendLine("AutoFix=" + Core.AutoFix);
                sb.AppendLine("# Also fix earlier words of the same passage once it is clear which language you meant");
                sb.AppendLine("Lookback=" + Core.Lookback);
                sb.AppendLine("# Hotkey: undo the last automatic fix, or flip what you just typed. Shift+hotkey flips selected text.");
                sb.AppendLine("Hotkey=" + Hotkey);
                sb.AppendLine("Hotkey2=" + (Hotkey2 == null ? "" : Hotkey2.ToString()));
                sb.AppendLine("# Apps where KeyFlip does nothing (process names, separated by ;)");
                sb.AppendLine("ExcludedApps=" + string.Join(";", ExcludedApps));
                sb.AppendLine("# Sites/windows where KeyFlip is off: any window whose title contains one of these (separated by ;)");
                sb.AppendLine("ExcludedSites=" + string.Join(";", ExcludedSites));
                sb.AppendLine("# Write every fix to fix-log.txt (helps tuning; it records the fixed words only)");
                sb.AppendLine("LogFixes=" + LogFixes);
                sb.AppendLine("# Advanced detection tuning");
                sb.AppendLine("MinWordLength=" + Core.MinWordLength);
                sb.AppendLine("MinScore=" + Core.MinScore.ToString(inv));
                sb.AppendLine("MinScoreShort=" + Core.MinScoreShort.ToString(inv));
                sb.AppendLine("Ratio=" + Core.Ratio.ToString(inv));
                sb.AppendLine("KeepIfTypedAbove=" + Core.KeepIfTypedAbove.ToString(inv));
                sb.AppendLine("ShownWelcome=" + ShownWelcome);
                File.WriteAllText(SettingsPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        public static List<string> LoadWords()
        {
            var list = new List<string>();
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(WordsPath))
                    File.WriteAllText(WordsPath,
                        "# Words KeyFlip should never change, one per line (in either language).\r\n" +
                        "# Add company terms, codes and names that it gets wrong. Lines starting with # are ignored.\r\n" +
                        "# Words you undo with the hotkey are added here automatically.\r\n", new UTF8Encoding(true));
                foreach (var l in File.ReadAllLines(WordsPath, Encoding.UTF8))
                {
                    var t = l.Trim();
                    if (t.Length > 0 && !t.StartsWith("#")) list.Add(t);
                }
            }
            catch { }
            return list;
        }

        public static void AddWord(string w)
        {
            try { File.AppendAllText(WordsPath, w + "\r\n", Encoding.UTF8); } catch { }
        }

        public static void Log(string line)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n", Encoding.UTF8); } catch { }
        }

        static bool Bool(string v, bool d) { bool b; if (bool.TryParse(v, out b)) return b; if (v == "1") return true; if (v == "0") return false; return d; }
        static int Int(string v, int d) { int i; return int.TryParse(v, out i) ? i : d; }
        static double Dbl(string v, double d) { double x; return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : d; }
    }
}
