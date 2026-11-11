// Engine simulator: runs the real Engine.cs against the simulated Windows input system in FakeWindows.cs,
// "types" scenarios with realistic timing, and reports for each one
//   - whether the final text is right,
//   - how long text was missing from the screen while a fix was being applied (the flicker people notice),
//   - how many 60 Hz display frames would have shown that missing text,
//   - how long it took from pressing space until the corrected text was on screen.
// Usage: dotnet run -c Release --project sim                  (from the repository root)
//        dotnet run -c Release --project sim -- --soak 40     (40 random-speed passages with overlapping key presses)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace KeyFlip
{
    class Typist
    {
        public const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_PAUSE = 0x13;
        readonly FakeOS os;
        public int GapMs = 80, HoldMs = 30;
        public Typist(FakeOS os) { this.os = os; }

        /// <summary>Press the keys that would produce `text` on a keyboard set to `keysOf`.</summary>
        public void Type(string text, Lang keysOf)
        {
            foreach (char c in text)
            {
                int vk; bool shift = false;
                if (c == ' ') vk = KeyMap.VK_SPACE;
                else if (c == '\n') vk = KeyMap.VK_RETURN;
                else if (!KeyMap.KeyFor(c, keysOf, out vk, out shift)) throw new Exception("no key for " + c);
                Tap(vk, shift);
                Thread.Sleep(Math.Max(1, GapMs - HoldMs));
            }
        }

        /// <summary>The user switches the keyboard language themselves (Alt+Shift).</summary>
        public void AltShift(FakeWindow w)
        {
            os.Phys(0xA4, true); os.Phys(VK_LSHIFT, true);
            Thread.Sleep(20);
            os.Phys(VK_LSHIFT, false); os.Phys(0xA4, false);
            w.Layout = w.Layout == Lang.He ? Lang.En : Lang.He;
            Thread.Sleep(40);
        }

        /// <summary>Like Type, but at random speed and with rollover: the next key often goes down before the last one is up.</summary>
        public void TypeRandom(string text, Lang keysOf, Random rnd, int maxGapMs)
        {
            int pendingUp = 0;
            foreach (char c in text)
            {
                int vk; bool shift = false;
                if (c == ' ') vk = KeyMap.VK_SPACE;
                else if (!KeyMap.KeyFor(c, keysOf, out vk, out shift)) throw new Exception("no key for " + c);
                bool rollover = pendingUp != 0 && !shift && rnd.Next(3) == 0;
                if (pendingUp != 0 && !rollover) { os.Phys(pendingUp, false); pendingUp = 0; }
                if (shift) os.Phys(VK_LSHIFT, true);
                os.Phys(vk, true);
                if (rollover) { Thread.Sleep(rnd.Next(1, 6)); os.Phys(pendingUp, false); }
                if (shift) { Thread.Sleep(rnd.Next(1, 10)); os.Phys(vk, false); os.Phys(VK_LSHIFT, false); pendingUp = 0; }
                else pendingUp = vk;
                Thread.Sleep(rnd.Next(2, maxGapMs));
            }
            if (pendingUp != 0) os.Phys(pendingUp, false);
        }

        public void Tap(int vk, bool shift = false, bool ctrl = false, int holdMs = -1)
        {
            if (ctrl) os.Phys(VK_LCONTROL, true);
            if (shift) os.Phys(VK_LSHIFT, true);
            os.Phys(vk, true);
            Thread.Sleep(holdMs < 0 ? HoldMs : holdMs);
            os.Phys(vk, false);
            if (shift) os.Phys(VK_LSHIFT, false);
            if (ctrl) os.Phys(VK_LCONTROL, false);
        }
    }

    class Scenario
    {
        public string Name, Expect, Text0 = "";
        public Lang Start;
        public int GapMs = 80, HoldMs = 30;
        public Action<FakeOS, FakeWindow> Setup;
        public Action<Typist, FakeOS> Run;
        public Func<FakeOS, FakeWindow, int, string> Check; // extra expectation; returns a complaint or null
    }

    static class Program
    {
        static Lexicon lex;

        static Stream Find(string name, string[] args)
        {
            var roots = new List<string>();
            if (args.Length > 0) roots.Add(args[0]);
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent) roots.Add(Path.Combine(d.FullName, "src", "Resources"));
            foreach (var r in roots)
                if (File.Exists(Path.Combine(r, name))) return new GZipStream(File.OpenRead(Path.Combine(r, name)), CompressionMode.Decompress);
            throw new FileNotFoundException("Could not find " + name + " - pass the path of src/Resources as the first argument.");
        }

        static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            using (var en = Find("en.txt.gz", args)) using (var he = Find("he.txt.gz", args)) lex = Lexicon.Load(en, he);

            const string wrongHe = "שלום מה שלומך ";
            var all = new List<Scenario>
            {
                new Scenario { Name = "Hebrew sentence on an English keyboard", Start = Lang.En, Expect = wrongHe,
                    Run = (t, os) => t.Type(wrongHe, Lang.He) },
                new Scenario { Name = "English sentence on a Hebrew keyboard", Start = Lang.He, Expect = "hello how are you ",
                    Run = (t, os) => t.Type("hello how are you ", Lang.En) },
                new Scenario { Name = "Earlier words fixed too (look-back)", Start = Lang.En, Expect = "גם אני רוצה ",
                    Run = (t, os) => t.Type("גם אני רוצה ", Lang.He) },
                new Scenario { Name = "Fix triggered by punctuation", Start = Lang.En, Expect = "תודה רבה. ",
                    Run = (t, os) => t.Type("תודה רבה. ", Lang.He) },
                new Scenario { Name = "Fix triggered by Enter", Start = Lang.He, Expect = "thanks\n",
                    Run = (t, os) => t.Type("thanks\n", Lang.En) },
                new Scenario { Name = "Correct English, then a Hebrew name", Start = Lang.En, Expect = "send to דני ",
                    Run = (t, os) => { t.Type("send to ", Lang.En); t.Type("דני ", Lang.He); } },
                new Scenario { Name = "Correct text is never touched", Start = Lang.En, Expect = "please send the report ",
                    Run = (t, os) => t.Type("please send the report ", Lang.En),
                    Check = (os, w, fixes) => fixes == 0 ? null : fixes + " fixes on correct text" },
                new Scenario { Name = "Very fast typist (28 ms between keys)", Start = Lang.En, GapMs = 28, HoldMs = 10,
                    Expect = "אני רוצה לשלוח את הדוח היום ",
                    Run = (t, os) => t.Type("אני רוצה לשלוח את הדוח היום ", Lang.He) },
                new Scenario { Name = "App slow to switch keyboard (40 ms)", Start = Lang.En, Expect = wrongHe,
                    Setup = (os, w) => w.SwitchCostMs = 40,
                    Run = (t, os) => t.Type(wrongHe, Lang.He) },
                new Scenario { Name = "Hosted app: focus on another thread", Start = Lang.En, Expect = "שלום מה שלומך היום ",
                    Setup = (os, w) => { w.SplitThreads = true; w.FocusTid = w.Tid + 1; },
                    Run = (t, os) => t.Type("שלום מה שלומך היום ", Lang.He) },
                new Scenario { Name = "Keyboard can't be read back (console style)", Start = Lang.En, Expect = "שלום מה שלומך היום ",
                    Setup = (os, w) => w.StaleQuery = true,
                    Run = (t, os) => t.Type("שלום מה שלומך היום ", Lang.He) },
                new Scenario { Name = "Slow password check while typing ahead", Start = Lang.En, GapMs = 45, HoldMs = 15, Expect = wrongHe,
                    Setup = (os, w) => os.ProbeCostMs = 100,
                    Run = (t, os) => t.Type(wrongHe, Lang.He) },
                new Scenario { Name = "Ctrl+S right after the wrong word", Start = Lang.En, Expect = "שלום ",
                    Setup = (os, w) => os.ProbeCostMs = 100,
                    Run = (t, os) =>
                    {
                        t.Type("שלום", Lang.He);
                        t.Tap(KeyMap.VK_SPACE, holdMs: 5); Thread.Sleep(5);
                        t.Tap('S', ctrl: true, holdMs: 5);   // pressed and released while the fix is still being prepared
                    },
                    Check = (os, w, fixes) => w.Shortcuts.Count(x => x == "Ctrl+S") == 1 ? null : "Ctrl+S not delivered (" + string.Join(",", w.Shortcuts) + ")" },
                new Scenario { Name = "Undo with the hotkey", Start = Lang.He, Expect = "יקךךם ",
                    Run = (t, os) => { t.Type("hello ", Lang.En); Thread.Sleep(300); t.Tap(Typist.VK_PAUSE); } },
                new Scenario { Name = "Flip selected text (Shift+hotkey)", Start = Lang.En, Text0 = "akuo", Expect = "שלום",
                    Setup = (os, w) => w.SelectedLen = 4,
                    Run = (t, os) => t.Tap(Typist.VK_PAUSE, shift: true) },
                new Scenario { Name = "Password box is left alone", Start = Lang.En, Expect = "akuo ",
                    Setup = (os, w) => os.FocusedIsPassword = true,
                    Run = (t, os) => t.Type("שלום ", Lang.He),
                    Check = (os, w, fixes) => fixes == 0 ? null : "acted in a password box" },
                new Scenario { Name = "Window changes while a fix is prepared", Start = Lang.En, Expect = "akuo",
                    Setup = (os, w) =>
                    {
                        var other = os.AddWindow(Lang.En, "KEEP");
                        os.ProbeCostMs = 30;
                        os.OnProbe = () => os.Foreground = other;   // e.g. a dialog pops up at that very moment
                    },
                    Run = (t, os) => t.Type("שלום ", Lang.He),
                    Check = (os, w, fixes) => os.Windows[1].Text == "KEEP" ? null : "typed into the other window: [" + os.Windows[1].Text + "]" },
            };

            all.Add(new Scenario { Name = "Editor that re-picks the keyboard on delete", Start = Lang.En,
                Expect = "see the דוח שלי כאן and the קובץ הזה ",
                Setup = (os, w) => w.AutoKeyboard = true,
                Run = (t, os) =>
                {
                    var w = os.Windows[0];
                    t.Type("see the ", Lang.En); t.Type("דוח שלי כאן ", Lang.He);   // first time: the app undoes the switch
                    t.AltShift(w);
                    t.Type("and the ", Lang.En); t.Type("קובץ הזה ", Lang.He);      // second time: KeyFlip already knows
                },
                Check = (os, w, fixes) => w.Layout == Lang.He ? null : "keyboard left in the wrong language" });
            all.Add(new Scenario { Name = "User switches language right after a fix", Start = Lang.En, Expect = "שלום ok then ",
                Run = (t, os) => { t.Type("שלום ", Lang.He); t.AltShift(os.Windows[0]); t.Type("ok then ", Lang.En); },
                Check = (os, w, fixes) => w.Layout == Lang.En ? null : "KeyFlip overrode the user's own switch" });

            // The same kind of text at speeds no person reaches, to shake out lost or reordered keys around a fix.
            string[] passages =
            {
                "I want to send the report today ",
                "היי דנה, מצרפת את הדוח החודשי של הלקוחות. אשמח אם תעברי על הנתונים לפני הפגישה מחר בבוקר. תודה! ",
            };
            foreach (int gap in new[] { 40, 14 })
                foreach (string passage in passages)
                {
                    string text = passage;
                    Lang meant = text.Any(KeyMap.IsHebrewLetter) ? Lang.He : Lang.En;
                    all.Add(new Scenario
                    {
                        Name = "Stress: " + (meant == Lang.He ? "Hebrew" : "English") + " passage, " + gap + " ms between keys",
                        Start = meant == Lang.He ? Lang.En : Lang.He, GapMs = gap, HoldMs = gap / 3, Expect = text,
                        Run = (t, os) => t.Type(text, meant)
                    });
                }

            int soak = Array.IndexOf(args, "--soak");
            if (soak >= 0)
            {
                // Whole passages started on the wrong keyboard, typed at random speeds. Whatever the timing, the text
                // must come out exactly as meant: no key lost, doubled or out of order around the fix.
                string[] texts =
                {
                    "היי דנה, מצרפת את הדוח החודשי של הלקוחות. אשמח אם תעברי על הנתונים לפני הפגישה מחר בבוקר. תודה! ",
                    "Hi team, please find attached the monthly report. Let me know if you have any questions before tomorrow's meeting. ",
                    "We need to update the dashboard KPIs and send it to the managers by Thursday, ok? ",
                    "אני בפגישה עד 14:00, אחזור אליך אחר כך ",
                    "As discussed, the new process starts on Sunday. Please confirm. ",
                };
                int n = soak + 1 < args.Length ? int.Parse(args[soak + 1]) : 20;
                var seedRnd = new Random(12345);
                all.Clear();
                for (int i = 0; i < n; i++)
                {
                    string text = texts[i % texts.Length];
                    Lang meant = text.Any(KeyMap.IsHebrewLetter) ? Lang.He : Lang.En;
                    int seed = seedRnd.Next(), maxGap = new[] { 8, 25, 60, 110 }[i % 4];
                    all.Add(new Scenario
                    {
                        Name = "Soak " + (i + 1) + ": " + (meant == Lang.He ? "Hebrew" : "English") + ", keys up to " + maxGap + " ms apart",
                        Start = meant == Lang.He ? Lang.En : Lang.He, Expect = text,
                        Setup = (os, w) => os.ProbeCostMs = 5 + seed % 60,
                        Run = (t, os) => t.TypeRandom(text, meant, new Random(seed), maxGap)
                    });
                }
            }

            Console.WriteLine("{0,-46} {1,-5} {2,5} {3,6} {4,10} {5,7} {6,11}", "scenario", "text", "fixes", "gaps", "longest ms", "frames", "space>fixed");
            int fails = 0; double worst = 0, total = 0; int frames = 0, gaps = 0;
            foreach (var s in all)
            {
                var r = RunOne(s);
                if (!r.Ok) fails++;
                worst = Math.Max(worst, r.Longest); total += r.Total; frames += r.Frames; gaps += r.Gaps;
                Console.WriteLine("{0,-46} {1,-5} {2,5} {3,6} {4,10:F1} {5,7} {6,11:F0}{7}", s.Name, r.Ok ? "ok" : "WRONG", r.Fixes, r.Gaps, r.Longest, r.Frames, r.SpaceToFixed,
                    r.Ok ? "" : "   got [" + r.Got.Replace("\n", "\\n") + "]" + (r.Complaint != null ? " " + r.Complaint : ""));
            }
            Console.WriteLine();
            Console.WriteLine("Text missing from the screen: {0} gaps, longest {1:F1} ms, {2:F0} ms in total, visible in {3} display frames (60 Hz).", gaps, worst, total, frames);
            Console.WriteLine(fails == 0 ? "ALL SCENARIOS END WITH THE RIGHT TEXT" : fails + " SCENARIOS WRONG");
            return fails == 0 ? 0 : 1;
        }

        class Result { public bool Ok; public string Got, Complaint; public int Fixes, Gaps, Frames; public double Longest, Total, SpaceToFixed; }

        static Result RunOne(Scenario s)
        {
            var os = new FakeOS(); FakeOS.Cur = os;
            var w = os.AddWindow(s.Start, s.Text0);
            if (s.Setup != null) s.Setup(os, w);
            var cfg = new Config();
            var engine = new Engine(lex, cfg);
            engine.ApplyConfig(cfg, new string[0]);
            int fixes = 0;
            engine.Fixed += (plan, app) => Interlocked.Increment(ref fixes);
            engine.Start();
            Thread.Sleep(100);

            s.Run(new Typist(os) { GapMs = s.GapMs, HoldMs = s.HoldMs }, os);
            os.WaitIdle(700);
            engine.Dispose();
            Thread.Sleep(60);
            os.Close();

            var r = new Result { Got = w.Text, Fixes = fixes };
            r.Complaint = s.Check == null ? null : s.Check(os, w, fixes);
            if (engine.LastError != null) r.Complaint = (r.Complaint ?? "") + " engine error: " + engine.LastError;
            r.Ok = r.Got == s.Expect && r.Complaint == null;

            // A "gap" starts when text is deleted and ends when the screen is at least as long again.
            Frame[] tl; lock (w.Timeline) tl = w.Timeline.ToArray();
            double[] downs; lock (os.KeyDowns) downs = os.KeyDowns.ToArray();
            int baseLen = -1; double start = 0;
            Action<double> close = end =>
            {
                double len = end - start;
                r.Gaps++; r.Total += len; r.Longest = Math.Max(r.Longest, len);
                r.Frames += (int)(Math.Floor(end / (1000.0 / 60)) - Math.Floor(start / (1000.0 / 60)));
                double pressed = downs.Where(d => d <= start).DefaultIfEmpty(start).Max();
                r.SpaceToFixed = Math.Max(r.SpaceToFixed, end - pressed);
            };
            for (int i = 1; i < tl.Length; i++)
            {
                int prev = tl[i - 1].Text.Length, cur = tl[i].Text.Length;
                if (baseLen < 0 && cur < prev) { baseLen = prev; start = tl[i].T; }
                else if (baseLen >= 0 && cur >= baseLen) { close(tl[i].T); baseLen = -1; }
            }
            if (baseLen >= 0) close(tl[tl.Length - 1].T);
            return r;
        }
    }
}
