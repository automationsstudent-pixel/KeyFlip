// The live part: keyboard/mouse hooks, and a worker thread that performs the fixes.
//
// Flow: the hook thread sees every key press (it does not log or store text beyond the current passage, in memory only).
// When the tracker decides a word was typed in the wrong layout, the hook holds back the space/punctuation key,
// and the worker switches the layout of that window and then replaces the word in ONE input batch (backspaces,
// corrected text and the held-back key together), so the text is never left half-deleted on screen.
// Keys typed while a fix is in progress are held and replayed afterwards, in order.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using N = KeyFlip.Native;

namespace KeyFlip
{
    public class Engine : IDisposable
    {
        static readonly IntPtr MARK_FIX = new IntPtr(0x4B460001);    // our own typing: ignored by the hook
        static readonly IntPtr MARK_REPLAY = new IntPtr(0x4B460002); // held-back user keys: tracked like real typing

        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                  VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_C = 0x43;

        public readonly object Sync = new object();
        readonly RunTracker tracker;
        readonly Lexicon lex;
        public Config Cfg;

        Thread hookThread, workerThread;
        uint hookThreadId;
        IntPtr kbHook, msHook;
        N.HookProc kbProc, msProc;   // kept in fields so the GC never collects the delegates
        readonly BlockingCollection<Job> jobs = new BlockingCollection<Job>();
        readonly ManualResetEvent hookReady = new ManualResetEvent(false);

        bool busy;
        bool capsOn = (N.GetKeyState(VK_CAPITAL) & 1) != 0; // tracked from key presses (the hook thread has no own key state)
        readonly Queue<Held> held = new Queue<Held>();
        readonly HashSet<int> swallowedUps = new HashSet<int>();
        IntPtr lastFg;
        bool fgExcluded;
        readonly Dictionary<uint, bool> pidExcluded = new Dictionary<uint, bool>();

        public event Action<FixPlan, string> Fixed;   // plan, app name
        public event Action<string> SitePausedChanged; // matched rule, or null when no longer paused
        public IntPtr LastTypedWindow;                  // for "Turn off on this site"
        string pausedRule;
        public string LastError;

        class Job
        {
            public FixPlan Plan; public bool Selection; public IntPtr Fg; public bool Manual; public string App;
            public Lang Reswitch = Lang.Other; // not a fix: just put the layout back to this (see HandleKey)
        }
        struct Held { public int Vk; public uint Scan; public bool Extended, Shift, Ctrl, Alt, Win; }

        // After a fix, the window and the layout we left it in: lets the hook notice an app that takes the switch back.
        IntPtr expectFg; Lang expectLang = Lang.Other; int expectAt;
        // Apps seen doing that. In those the layout is switched after the old text is deleted instead of before.
        readonly HashSet<string> deleteFirstApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Worker thread only.
        readonly List<Action> afterFix = new List<Action>();          // slow follow-ups (disk, tray icon), run once typing flows again
        readonly HashSet<IntPtr> unconfirmed = new HashSet<IntPtr>(); // windows that did not confirm a layout switch last time

        // Password check (UI Automation), on its own long-lived thread so a slow app can never stall typing.
        Thread probeThread;
        readonly AutoResetEvent probeGo = new AutoResetEvent(false);
        readonly object probeLock = new object();
        int probeAsked, probeDone;
        bool probeResult;
        volatile bool disposed;

        public Engine(Lexicon lex, Config cfg)
        {
            this.lex = lex; Cfg = cfg;
            tracker = new RunTracker(lex, cfg.Core);
        }

        public bool Enabled
        {
            get { return Cfg.Enabled; }
            set { lock (Sync) { Cfg.Enabled = value; tracker.Reset(); } }
        }

        public void ApplyConfig(Config cfg, IEnumerable<string> words)
        {
            lock (Sync)
            {
                Cfg = cfg; tracker.Settings = cfg.Core; tracker.Reset();
                lex.UserWords.Clear();
                foreach (var w in words) lex.UserWords.Add(w);
                pidExcluded.Clear(); lastFg = IntPtr.Zero;
            }
        }

        public void Start()
        {
            workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "KeyFlip worker" };
            workerThread.TrySetApartmentState(ApartmentState.STA); // clipboard access
            workerThread.Start();
            probeThread = new Thread(ProbeLoop) { IsBackground = true, Name = "KeyFlip password check" };
            probeThread.Start();
            hookThread = new Thread(HookLoop) { IsBackground = true, Name = "KeyFlip hook", Priority = ThreadPriority.AboveNormal };
            hookThread.Start();
            hookReady.WaitOne(3000);
        }

        public void Dispose()
        {
            if (hookThreadId != 0) N.PostThreadMessage(hookThreadId, N.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            jobs.CompleteAdding();
            disposed = true;
            probeGo.Set();
        }

        // ------------------------------------------------------------------ hook thread

        void HookLoop()
        {
            hookThreadId = N.GetCurrentThreadId();
            kbProc = KeyboardProc; msProc = MouseProc;
            IntPtr mod = N.GetModuleHandle(null);
            kbHook = N.SetWindowsHookEx(N.WH_KEYBOARD_LL, kbProc, mod, 0);
            msHook = N.SetWindowsHookEx(N.WH_MOUSE_LL, msProc, mod, 0);
            if (kbHook == IntPtr.Zero) LastError = "Could not install the keyboard hook (error " + Marshal.GetLastWin32Error() + ").";
            hookReady.Set();
            N.MSG m;
            while (N.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { N.TranslateMessage(ref m); N.DispatchMessage(ref m); }
            if (kbHook != IntPtr.Zero) N.UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) N.UnhookWindowsHookEx(msHook);
        }

        IntPtr MouseProc(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                int msg = w.ToInt32();
                if (msg == N.WM_LBUTTONDOWN || msg == N.WM_RBUTTONDOWN || msg == N.WM_MBUTTONDOWN || msg == N.WM_XBUTTONDOWN)
                    lock (Sync) { tracker.Reset(); expectLang = Lang.Other; } // caret probably moved: forget the current passage
            }
            return N.CallNextHookEx(msHook, nCode, w, l);
        }

        IntPtr KeyboardProc(int nCode, IntPtr w, IntPtr l)
        {
            if (nCode >= 0)
            {
                try
                {
                    var kb = (N.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(N.KBDLLHOOKSTRUCT));
                    if (HandleKey(w.ToInt32(), kb)) return new IntPtr(1);
                }
                catch (Exception ex) { LastError = ex.Message; }
            }
            return N.CallNextHookEx(kbHook, nCode, w, l);
        }

        static bool IsDown(int vk) { return (N.GetAsyncKeyState(vk) & 0x8000) != 0; }

        static bool IsModifierOrLock(int vk)
        {
            return vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU || (vk >= 0xA0 && vk <= 0xA5) ||
                   vk == VK_LWIN || vk == VK_RWIN || vk == VK_CAPITAL || vk == 0x90 || vk == 0x91;
        }

        bool MatchHotkey(int vk, bool ctrl, bool alt, bool shift, bool win, out bool selection)
        {
            selection = false;
            foreach (var hk in new[] { Cfg.Hotkey, Cfg.Hotkey2 })
            {
                if (hk == null || (int)hk.Key != vk || hk.Ctrl != ctrl || hk.Alt != alt || hk.Win != win) continue;
                if (hk.Shift == shift) return true;
                if (!hk.Shift && shift) { selection = true; return true; }
            }
            return false;
        }

        /// <summary>Returns true to swallow the key.</summary>
        bool HandleKey(int msg, N.KBDLLHOOKSTRUCT kb)
        {
            bool injected = (kb.flags & N.LLKHF_INJECTED) != 0;
            if (injected && kb.dwExtraInfo == MARK_FIX) return false;
            bool replay = injected && kb.dwExtraInfo == MARK_REPLAY;
            if (injected && !replay) return false; // other software typing: leave it alone
            int vk = (int)kb.vkCode;
            bool down = msg == N.WM_KEYDOWN || msg == N.WM_SYSKEYDOWN;

            lock (Sync)
            {
                if (!down) return swallowedUps.Remove(vk);
                if (vk == VK_CAPITAL && !replay) capsOn = !capsOn;
                if (IsModifierOrLock(vk) && vk != VK_SHIFT && vk != VK_LSHIFT && vk != VK_RSHIFT)
                    expectLang = Lang.Other; // Ctrl/Alt/Win/locks: the user may be switching the layout themselves
                if (!Cfg.Enabled || IsModifierOrLock(vk)) return false;

                bool shift = IsDown(VK_SHIFT), ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
                bool selection = false;
                bool isHotkey = !replay && MatchHotkey(vk, ctrl, alt, shift, win, out selection);

                if (busy && !replay)
                {
                    swallowedUps.Add(vk);
                    if (!isHotkey) held.Enqueue(HeldKey(vk, kb, shift, ctrl, alt, win));
                    return true;
                }

                IntPtr fg = N.GetForegroundWindow();
                CheckWindow(fg);

                if (isHotkey)
                {
                    if (fgExcluded) return false;
                    expectLang = Lang.Other;
                    swallowedUps.Add(vk);
                    FixPlan plan = selection ? null : tracker.Manual();
                    busy = true;
                    jobs.Add(new Job { Plan = plan, Selection = plan == null, Fg = fg, Manual = true, App = AppName(fg) });
                    return true;
                }

                if (ctrl || alt || win) { tracker.Reset(); expectLang = Lang.Other; return false; }
                if (fgExcluded) return false;
                LastTypedWindow = fg;
                if (SiteExcluded(fg)) { tracker.Reset(); return false; }

                Lang lang = LangOf(fg);
                if (lang == Lang.Other) { tracker.Reset(); return false; }

                if (expectLang != Lang.Other && !replay)
                {
                    bool typing = vk == KeyMap.VK_SPACE || KeyMap.IsPrintable(vk);
                    if (!typing || fg != expectFg || unchecked(Environment.TickCount - expectAt) > 1000)
                        expectLang = Lang.Other; // deleting or moving around, another window, or simply a while ago
                    else if (lang != expectLang)
                    {
                        // The user just kept typing after a fix, yet the layout is no longer the one the fix switched
                        // to: the app took the switch back (some editors pick the keyboard themselves while text is
                        // being deleted). Hold this key, switch again, then let the key through - and from now on
                        // delete first and switch last in this app.
                        Lang want = expectLang;
                        expectLang = Lang.Other;
                        string app = AppName(fg);
                        deleteFirstApps.Add(app);
                        busy = true;
                        swallowedUps.Add(vk);
                        held.Enqueue(HeldKey(vk, kb, shift, ctrl, alt, win));
                        jobs.Add(new Job { Fg = fg, App = app, Reswitch = want });
                        return true;
                    }
                }

                if (KeyMap.IsLetterKey(vk) && capsOn) shift = !shift;

                FixPlan p;
                if (vk == KeyMap.VK_BACK) { tracker.Backspace(); return false; }
                if (KeyMap.IsWordKey(vk, shift)) { tracker.AddWordUnit(new Unit(UnitKind.Word, vk, shift, lang)); return false; }

                bool boundary = vk == KeyMap.VK_SPACE || vk == KeyMap.VK_RETURN || vk == KeyMap.VK_TAB || KeyMap.IsPrintable(vk);
                if (!boundary) { tracker.Reset(); return false; } // arrows, Home, Delete, Esc, F-keys...

                if (IsNativePasswordField(fg)) { tracker.Reset(); return false; }
                var unit = new Unit(vk == KeyMap.VK_SPACE ? UnitKind.Space : UnitKind.Neutral, vk, shift, lang);
                bool resetAfter = vk == KeyMap.VK_RETURN || vk == KeyMap.VK_TAB;
                p = tracker.Boundary(unit, resetAfter, !replay);
                if (p == null) return false;

                busy = true;
                swallowedUps.Add(vk);
                jobs.Add(new Job { Plan = p, Fg = fg, App = AppName(fg) });
                return true;
            }
        }

        static Held HeldKey(int vk, N.KBDLLHOOKSTRUCT kb, bool shift, bool ctrl, bool alt, bool win)
        {
            return new Held
            {
                Vk = vk, Scan = kb.scanCode, Extended = (kb.flags & N.LLKHF_EXTENDED) != 0,
                Shift = shift, Ctrl = ctrl, Alt = alt, Win = win
            };
        }

        public static string WindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            N.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Is the window title (which in browsers includes the page/site name) on the user's "off here" list?</summary>
        bool SiteExcluded(IntPtr fg)
        {
            string rule = null;
            if (Cfg.ExcludedSites.Count > 0)
            {
                string title = WindowTitle(fg);
                foreach (var r in Cfg.ExcludedSites)
                    if (title.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0) { rule = r; break; }
            }
            if (rule != pausedRule)
            {
                pausedRule = rule;
                var h = SitePausedChanged; if (h != null) h(rule);
            }
            return rule != null;
        }

        void CheckWindow(IntPtr fg)
        {
            if (fg == lastFg) return;
            lastFg = fg;
            tracker.Reset();
            uint pid;
            N.GetWindowThreadProcessId(fg, out pid);
            bool ex;
            if (!pidExcluded.TryGetValue(pid, out ex))
            {
                string exe = ProcessExe(pid);
                ex = exe == null || Cfg.ExcludedApps.Contains(exe) || !CanTypeInto(pid) ||
                     pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id; // never act in KeyFlip's own windows
                pidExcluded[pid] = ex;
            }
            fgExcluded = ex;
        }

        readonly Dictionary<uint, string> pidNames = new Dictionary<uint, string>();
        string ProcessExe(uint pid)
        {
            string name;
            if (pidNames.TryGetValue(pid, out name)) return name;
            name = null;
            IntPtr h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                if (N.QueryFullProcessImageName(h, 0, sb, ref size)) name = Path.GetFileName(sb.ToString());
                N.CloseHandle(h);
            }
            pidNames[pid] = name;
            return name;
        }

        string AppName(IntPtr fg) { uint pid; N.GetWindowThreadProcessId(fg, out pid); return ProcessExe(pid) ?? "?"; }

        /// <summary>Windows blocks typing into apps running as administrator; skip those instead of losing keys.</summary>
        static bool CanTypeInto(uint pid)
        {
            IntPtr h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            IntPtr tok;
            bool ok = N.OpenProcessToken(h, N.TOKEN_QUERY, out tok);
            if (ok) N.CloseHandle(tok);
            N.CloseHandle(h);
            return ok;
        }

        /// <summary>
        /// The thread whose keyboard layout counts: the one that owns the focused control. In apps that show their
        /// content through another process or thread (embedded web views, Store apps) this is not the thread of the
        /// top-level window, and asking that one can give a layout that never changes.
        /// </summary>
        static uint InputThread(IntPtr fg, out IntPtr focus)
        {
            uint pid;
            uint tid = N.GetWindowThreadProcessId(fg, out pid);
            focus = fg;
            var gi = new N.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(N.GUITHREADINFO)) };
            if (N.GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero)
            {
                uint focusTid = N.GetWindowThreadProcessId(gi.hwndFocus, out pid);
                if (focusTid != 0) { focus = gi.hwndFocus; tid = focusTid; }
            }
            return tid;
        }

        static Lang LangOf(IntPtr fg)
        {
            IntPtr focus;
            return LangOfHkl(N.GetKeyboardLayout(InputThread(fg, out focus)));
        }

        static Lang LangOfHkl(IntPtr hkl)
        {
            int primary = (int)(hkl.ToInt64() & 0x3FF);
            if (primary == 0x0D) return Lang.He;
            if (primary == 0x09) return Lang.En;
            return Lang.Other;
        }

        static bool IsNativePasswordField(IntPtr fg)
        {
            uint pid;
            uint tid = N.GetWindowThreadProcessId(fg, out pid);
            var gi = new N.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(N.GUITHREADINFO)) };
            if (!N.GetGUIThreadInfo(tid, ref gi) || gi.hwndFocus == IntPtr.Zero) return false;
            return (N.GetWindowLong(gi.hwndFocus, N.GWL_STYLE) & N.ES_PASSWORD) != 0;
        }

        // ------------------------------------------------------------------ worker thread

        void WorkerLoop()
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { Execute(job); }
                catch (Exception ex) { LastError = ex.Message; Config.Log("error: " + ex); }
                finally { ReplayHeld(); }

                // Only now, with the user's typing flowing again, do the slow parts (files, tray icon).
                foreach (var follow in afterFix)
                {
                    try { follow(); }
                    catch (Exception ex) { LastError = ex.Message; }
                }
                afterFix.Clear();
            }
        }

        /// <summary>True if the user went to another window since the key press that started this job.</summary>
        bool WindowChanged(Job job)
        {
            if (N.GetForegroundWindow() == job.Fg) return false;
            lock (Sync) tracker.Reset();
            return true;
        }

        void Execute(Job job)
        {
            if (job.Manual)
            {
                WaitForModifiersUp(1500);
                if (WindowChanged(job)) return;
            }
            if (job.Reswitch != Lang.Other)
            {
                if (!WindowChanged(job)) SwitchLayoutFor(job.Fg, job.Reswitch);
                return;
            }
            if (job.Selection) { ConvertSelection(job); return; }
            var plan = job.Plan;
            if (plan == null) return;

            if (plan.Kind == "auto")
            {
                if (IsFocusedPasswordField())
                {
                    // never touch passwords: just deliver the key we held back
                    if (plan.Boundary != null) Send(KeyPress(plan.Boundary.Vk, plan.Boundary.Shift, MARK_FIX));
                    lock (Sync) tracker.Reset();
                    return;
                }
                // That check takes a moment. If another window came to the front meanwhile, the word is no longer
                // under the caret: leave everything as it is rather than delete text somewhere else.
                if (WindowChanged(job)) return;
            }

            var inputs = new List<N.INPUT>(2 * (plan.Backspaces + plan.Retype.Count) + 8);
            for (int i = 0; i < plan.Backspaces; i++) inputs.AddRange(Key(KeyMap.VK_BACK, MARK_FIX));
            bool deleteFirst;
            lock (Sync) deleteFirst = deleteFirstApps.Contains(job.App);
            bool confirmed;
            if (!deleteFirst)
            {
                // The order decides how a fix looks. First get the layout switched, which changes nothing on screen.
                // Then delete and retype in a single SendInput call: Windows delivers one call as an unbroken burst,
                // so the app goes from the wrong text to the right text with no moment where the word is just gone.
                confirmed = SwitchLayoutFor(job.Fg, plan.SwitchTo);
                AddRetype(inputs, plan);
                Send(inputs);
            }
            else
            {
                // This app re-picks the keyboard while text is deleted, so a switch made up front would be lost.
                // Delete, give the app a moment to finish with that, switch, and only then type. The word is briefly
                // missing from the screen here, but it is typed in the right layout.
                Send(inputs);
                N.timeBeginPeriod(1);
                Thread.Sleep(Math.Min(40, 3 + 2 * plan.Backspaces));
                N.timeEndPeriod(1);
                confirmed = SwitchLayoutFor(job.Fg, plan.SwitchTo);
                inputs.Clear();
                AddRetype(inputs, plan);
                Send(inputs);
            }
            if (confirmed) Expect(job.Fg, plan.SwitchTo);

            if (!string.IsNullOrEmpty(plan.LearnWord)) lock (Sync) lex.UserWords.Add(plan.LearnWord);
            string app = job.App;
            afterFix.Add(() =>
            {
                if (!string.IsNullOrEmpty(plan.LearnWord)) Config.AddWord(plan.LearnWord);
                if (Cfg.LogFixes) Config.Log(app + "  " + plan.Kind + ": " + plan.Before + "  ->  " + plan.After);
                var h = Fixed; if (h != null) h(plan, app);
            });
        }

        /// <summary>The corrected text plus the key that was held back. Built right before sending (it looks at Shift).</summary>
        static void AddRetype(List<N.INPUT> inputs, FixPlan plan)
        {
            foreach (var u in plan.Retype)
            {
                if (u.Kind == UnitKind.Word) inputs.AddRange(Unicode(u.CharIn(plan.SwitchTo)));
                else if (u.Kind == UnitKind.Space) inputs.AddRange(Key(KeyMap.VK_SPACE, MARK_FIX));
                else inputs.AddRange(KeyPress(u.Vk, u.Shift, MARK_FIX));
            }
            if (plan.Boundary != null) inputs.AddRange(KeyPress(plan.Boundary.Vk, plan.Boundary.Shift, MARK_FIX));
        }

        /// <summary>Remember the layout a fix left the window in, so the hook can tell if the app takes it back.</summary>
        void Expect(IntPtr fg, Lang lang)
        {
            lock (Sync) { expectFg = fg; expectLang = lang; expectAt = Environment.TickCount; }
        }

        void ReplayHeld()
        {
            while (true)
            {
                Held[] items;
                lock (Sync)
                {
                    if (held.Count == 0) { busy = false; return; }
                    items = held.ToArray(); held.Clear();
                }
                var inputs = new List<N.INPUT>();
                foreach (var k in items) inputs.AddRange(Replay(k));
                Send(inputs);
            }
        }

        /// <summary>
        /// Re-send a key that was held back during a fix, the way it was pressed: same physical key, and with
        /// Ctrl/Alt/Win if they were down then but have been released since (so Ctrl+S never turns into a typed "s").
        /// </summary>
        static List<N.INPUT> Replay(Held k)
        {
            bool ctrl = k.Ctrl && !IsDown(VK_CONTROL), alt = k.Alt && !IsDown(VK_MENU),
                 win = k.Win && !IsDown(VK_LWIN) && !IsDown(VK_RWIN);
            var list = new List<N.INPUT>();
            if (ctrl) list.Add(KeyEvent(VK_LCONTROL, false, MARK_FIX));
            if (alt) list.Add(KeyEvent(VK_LMENU, false, MARK_FIX));
            if (win) list.Add(KeyEvent(VK_LWIN, false, MARK_FIX, 0, true));
            list.AddRange(KeyPress(k.Vk, k.Shift, MARK_REPLAY, k.Scan, k.Extended));
            if (win) list.Add(KeyEvent(VK_LWIN, true, MARK_FIX, 0, true));
            if (alt) list.Add(KeyEvent(VK_LMENU, true, MARK_FIX));
            if (ctrl) list.Add(KeyEvent(VK_LCONTROL, true, MARK_FIX));
            return list;
        }

        static void WaitForModifiersUp(int maxMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(maxMs);
            while (DateTime.UtcNow < until && (IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN)))
                Thread.Sleep(10);
        }

        static IntPtr FindLayout(Lang lang)
        {
            int n = N.GetKeyboardLayoutList(0, null);
            var list = new IntPtr[Math.Max(n, 1)];
            N.GetKeyboardLayoutList(list.Length, list);
            foreach (var hkl in list) if (hkl != IntPtr.Zero && LangOfHkl(hkl) == lang) return hkl;
            return IntPtr.Zero;
        }

        public static bool HasBothLayouts() { return FindLayout(Lang.He) != IntPtr.Zero && FindLayout(Lang.En) != IntPtr.Zero; }

        /// <summary>
        /// Ask the window to switch layout and wait until it has, for at most maxMs. Nothing changes on screen
        /// while we wait, and the wait ends the moment the switch shows up (checked every millisecond).
        /// </summary>
        static bool SwitchLayout(IntPtr fg, Lang lang, int maxMs)
        {
            IntPtr hkl = FindLayout(lang);
            if (hkl == IntPtr.Zero) return false;
            IntPtr target;
            uint tid = InputThread(fg, out target);
            if (LangOfHkl(N.GetKeyboardLayout(tid)) == lang) return true;
            N.PostMessage(target, N.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
            bool retried = target == fg;
            var waited = Stopwatch.StartNew();
            N.timeBeginPeriod(1); // makes Sleep(1) really about 1 ms, only for the duration of this wait
            try
            {
                while (true)
                {
                    if (LangOfHkl(N.GetKeyboardLayout(tid)) == lang) return true;
                    long ms = waited.ElapsedMilliseconds;
                    if (ms >= maxMs) return false;
                    if (!retried && ms >= maxMs / 2)
                    {
                        retried = true; // the focused control didn't react: ask the window itself
                        N.PostMessage(fg, N.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
                    }
                    Thread.Sleep(1);
                }
            }
            finally { N.timeEndPeriod(1); }
        }

        /// <summary>
        /// Switch the layout, giving the app up to 400 ms to confirm. A few kinds of window never report their layout
        /// back; once a window has failed to confirm it only gets a short wait, so it can't stall every later fix.
        /// </summary>
        bool SwitchLayoutFor(IntPtr fg, Lang lang)
        {
            if (SwitchLayout(fg, lang, unconfirmed.Contains(fg) ? 60 : 400)) { unconfirmed.Remove(fg); return true; }
            if (unconfirmed.Count > 200) unconfirmed.Clear();
            unconfirmed.Add(fg);
            return false;
        }

        /// <summary>
        /// Browser and modern-app password boxes, via UI Automation. The question is put to the password-check
        /// thread and we wait at most 150 ms for the answer, so a slow or hung app can't hang typing.
        /// </summary>
        bool IsFocusedPasswordField()
        {
            lock (probeLock)
            {
                int id = ++probeAsked;
                probeGo.Set();
                var waited = Stopwatch.StartNew();
                while (probeDone < id)
                {
                    int left = 150 - (int)waited.ElapsedMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(probeLock, left);
                }
                return probeResult;
            }
        }

        void ProbeLoop()
        {
            // Load UI Automation now rather than during the first fix, where the load alone could use up the 150 ms.
            try { GC.KeepAlive(AutomationElement.RootElement); } catch { }
            while (true)
            {
                probeGo.WaitOne();
                if (disposed) return;
                int id;
                lock (probeLock) id = probeAsked; // the check below starts after this question was asked
                bool password = false;
                try
                {
                    var el = AutomationElement.FocusedElement;
                    if (el != null) password = (bool)el.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty);
                }
                catch { }
                lock (probeLock) { probeDone = id; probeResult = password; Monitor.PulseAll(probeLock); }
            }
        }

        void ConvertSelection(Job job)
        {
            IDataObject saved = null;
            try
            {
                var d = Clipboard.GetDataObject();
                if (d != null)
                {
                    var copy = new DataObject();
                    foreach (var f in d.GetFormats(false))
                        try { var o = d.GetData(f, false); if (o != null) copy.SetData(f, false, o); } catch { }
                    saved = copy;
                }
            }
            catch { }

            string text = null;
            try
            {
                Clipboard.Clear();
                var copyKeys = new List<N.INPUT>();
                copyKeys.Add(KeyEvent(VK_CONTROL, false, MARK_FIX));
                copyKeys.AddRange(Key(VK_C, MARK_FIX));
                copyKeys.Add(KeyEvent(VK_CONTROL, true, MARK_FIX));
                Send(copyKeys);
                for (int i = 0; i < 50 && text == null; i++)
                {
                    Thread.Sleep(10);
                    try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
                }
            }
            finally
            {
                try { if (saved != null) Clipboard.SetDataObject(saved, true); else Clipboard.Clear(); } catch { }
            }
            if (string.IsNullOrEmpty(text) || text.Length > 5000) return;

            Lang from = KeyMap.DetectScript(text);
            if (from == Lang.Other) return;
            Lang to = from == Lang.He ? Lang.En : Lang.He;
            string conv = KeyMap.ConvertText(text, from, to);

            var typing = new List<N.INPUT>();
            for (int i = 0; i < conv.Length; i++)
            {
                char c = conv[i];
                if (c == '\r') continue;
                if (c == '\n') typing.AddRange(Key(KeyMap.VK_RETURN, MARK_FIX));
                else if (c == '\t') typing.AddRange(Key(KeyMap.VK_TAB, MARK_FIX));
                else typing.AddRange(Unicode(c));
            }
            Send(typing); // typing over the selection replaces it
            if (SwitchLayoutFor(job.Fg, to)) Expect(job.Fg, to);
            lock (Sync) tracker.Reset();
            var plan = new FixPlan { Kind = "selection", Before = text, After = conv, SwitchTo = to };
            string app = job.App;
            afterFix.Add(() =>
            {
                if (Cfg.LogFixes) Config.Log(app + "  selection: " + text + "  ->  " + conv);
                var h = Fixed; if (h != null) h(plan, app);
            });
        }

        // ------------------------------------------------------------------ SendInput helpers

        /// <summary>scan/extended describe the physical key when we are replaying one; otherwise they are derived from vk.</summary>
        static N.INPUT KeyEvent(int vk, bool up, IntPtr mark, uint scan = 0, bool extended = false)
        {
            var inp = new N.INPUT { type = N.INPUT_KEYBOARD };
            inp.U.ki.wVk = (ushort)vk;
            inp.U.ki.wScan = (ushort)(scan != 0 ? scan : N.MapVirtualKey((uint)vk, 0));
            inp.U.ki.dwFlags = (up ? N.KEYEVENTF_KEYUP : 0) | (extended ? N.KEYEVENTF_EXTENDEDKEY : 0);
            inp.U.ki.dwExtraInfo = mark;
            return inp;
        }

        static IEnumerable<N.INPUT> Key(int vk, IntPtr mark, uint scan = 0, bool extended = false)
        {
            yield return KeyEvent(vk, false, mark, scan, extended);
            yield return KeyEvent(vk, true, mark, scan, extended);
        }

        /// <summary>Press a key with the given Shift state, whatever the physical Shift key is doing right now.</summary>
        static List<N.INPUT> KeyPress(int vk, bool shift, IntPtr mark, uint scan = 0, bool extended = false)
        {
            var list = new List<N.INPUT>();
            bool phys = IsDown(VK_SHIFT);
            if (shift && !phys) list.Add(KeyEvent(VK_LSHIFT, false, MARK_FIX));
            if (!shift && phys) list.Add(KeyEvent(VK_LSHIFT, true, MARK_FIX));
            list.AddRange(Key(vk, mark, scan, extended));
            if (shift && !phys) list.Add(KeyEvent(VK_LSHIFT, true, MARK_FIX));
            if (!shift && phys) list.Add(KeyEvent(VK_LSHIFT, false, MARK_FIX));
            return list;
        }

        static IEnumerable<N.INPUT> Unicode(char c)
        {
            for (int up = 0; up < 2; up++)
            {
                var inp = new N.INPUT { type = N.INPUT_KEYBOARD };
                inp.U.ki.wVk = 0;
                inp.U.ki.wScan = c;
                inp.U.ki.dwFlags = N.KEYEVENTF_UNICODE | (up == 1 ? N.KEYEVENTF_KEYUP : 0);
                inp.U.ki.dwExtraInfo = MARK_FIX;
                yield return inp;
            }
        }

        static void Send(IEnumerable<N.INPUT> inputs)
        {
            var arr = new List<N.INPUT>(inputs).ToArray();
            if (arr.Length == 0) return;
            N.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(N.INPUT)));
        }
    }
}
