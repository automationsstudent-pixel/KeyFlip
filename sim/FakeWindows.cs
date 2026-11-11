// A small managed stand-in for the Windows input system, so the real src/Engine.cs can be exercised on any OS.
//
// It replaces src/Native.cs (same names and signatures) and the few WinForms / UI Automation types the engine touches.
// What it models: the low-level keyboard hook (called on the hook thread, one key at a time), SendInput batches that
// cannot be interleaved with physical keys, per-thread keyboard layouts changed by WM_INPUTLANGCHANGEREQUEST, and an
// "app" that turns key events into text. Every change of the app's text is timestamped, which is what lets the
// simulator measure how long text is missing from the screen during a fix.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KeyFlip
{
    class Frame { public double T; public string Text; }

    /// <summary>One simulated application window with its own message loop.</summary>
    class FakeWindow
    {
        public IntPtr Hwnd, Focus;
        public uint Pid, Tid, FocusTid;
        public string Title = "Untitled - Notepad", Exe = "notepad.exe";
        public Lang Layout = Lang.En;      // layout of the thread that really receives the typing
        public Lang InitialLayout = Lang.En;
        public bool SplitThreads;          // keyboard focus lives on another thread than the top-level window (hosted / packaged apps)
        public bool StaleQuery;            // the layout can never be read back (console-style windows)
        public bool AutoKeyboard;          // the app re-picks the keyboard to match the text next to the caret whenever text is deleted
        public double KeyCostMs = 0.15;    // time the app spends handling one key event
        public double SwitchCostMs = 3;    // time the app spends acting on a layout-change request
        public int SelectedLen;            // the last N characters are selected (for the selection-flip scenario)
        public readonly StringBuilder Screen = new StringBuilder();
        public readonly List<Frame> Timeline = new List<Frame>();
        public readonly List<string> Shortcuts = new List<string>();

        class Ev { public bool Posted, Up, Unicode; public IntPtr L; public int Vk; public char Ch; }
        readonly ConcurrentQueue<Ev> posted = new ConcurrentQueue<Ev>(), input = new ConcurrentQueue<Ev>();
        readonly SemaphoreSlim work = new SemaphoreSlim(0);
        readonly HashSet<int> down = new HashSet<int>();
        readonly FakeOS os;
        volatile bool stop;
        public volatile int Pending;

        public FakeWindow(FakeOS os, int id, Lang layout, string text)
        {
            this.os = os;
            Hwnd = new IntPtr(0x1000 * id); Focus = new IntPtr(0x1000 * id + 1);
            Pid = (uint)(500 + id); Tid = (uint)(9000 + id * 10); FocusTid = Tid;
            Layout = InitialLayout = layout;
            Screen.Append(text ?? "");
            Record();
            new Thread(Loop) { IsBackground = true, Name = "fake app " + id }.Start();
        }

        public void Close() { stop = true; work.Release(); }
        public string Text { get { lock (Timeline) return Screen.ToString(); } }
        public double LastChange { get { lock (Timeline) return Timeline[Timeline.Count - 1].T; } }

        public void PostLayoutRequest(IntPtr hkl)
        {
            Interlocked.Increment(ref Pending);
            posted.Enqueue(new Ev { Posted = true, L = hkl }); work.Release();
        }

        public void Key(int vk, bool up, bool unicode, char ch)
        {
            Interlocked.Increment(ref Pending);
            input.Enqueue(new Ev { Vk = vk, Up = up, Unicode = unicode, Ch = ch }); work.Release();
        }

        void Record() { lock (Timeline) Timeline.Add(new Frame { T = os.Now, Text = Screen.ToString() }); }

        void Loop()
        {
            while (true)
            {
                work.Wait();
                if (stop) return;
                Ev e;
                // Like a real message loop: posted messages are handled before pending keyboard input.
                if (posted.TryDequeue(out e))
                {
                    FakeOS.BusyWait(SwitchCostMs);
                    Layout = FakeOS.LangOf(e.L);
                }
                else if (input.TryDequeue(out e))
                {
                    FakeOS.BusyWait(KeyCostMs);
                    Handle(e);
                }
                Interlocked.Decrement(ref Pending);
            }
        }

        bool Any(params int[] vks) { foreach (int v in vks) if (down.Contains(v)) return true; return false; }

        void Handle(Ev e)
        {
            if (e.Unicode) { if (!e.Up) Put(e.Ch); return; }
            if (e.Up) { down.Remove(e.Vk); return; }
            bool isMod = (e.Vk >= 0x10 && e.Vk <= 0x12) || (e.Vk >= 0xA0 && e.Vk <= 0xA5) || e.Vk == 0x5B || e.Vk == 0x5C;
            if (isMod) { down.Add(e.Vk); return; }
            bool shift = Any(0x10, 0xA0, 0xA1), ctrl = Any(0x11, 0xA2, 0xA3), alt = Any(0x12, 0xA4, 0xA5), win = Any(0x5B, 0x5C);
            if (ctrl || alt || win)
            {
                string name = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (win ? "Win+" : "") + (shift ? "Shift+" : "") + (char)e.Vk;
                lock (Timeline) Shortcuts.Add(name);
                if (ctrl && !alt && !win && e.Vk == 'C' && SelectedLen > 0)
                    lock (Timeline) os.ClipboardText = Screen.ToString(Screen.Length - SelectedLen, SelectedLen);
                return;
            }
            if (e.Vk == KeyMap.VK_BACK)
            {
                lock (Timeline) { if (SelectedLen > 0) DropSelection(); else if (Screen.Length > 0) Screen.Length--; }
                if (AutoKeyboard)
                    for (int i = Screen.Length - 1; i >= 0; i--)
                    {
                        if (KeyMap.IsHebrewLetter(Screen[i])) { Layout = Lang.He; break; }
                        if (KeyMap.IsLatin(Screen[i])) { Layout = Lang.En; break; }
                    }
                Record(); return;
            }
            if (e.Vk == KeyMap.VK_SPACE) { Put(' '); return; }
            if (e.Vk == KeyMap.VK_RETURN) { Put('\n'); return; }
            if (e.Vk == KeyMap.VK_TAB) { Put('\t'); return; }
            if (KeyMap.IsPrintable(e.Vk)) { Put(KeyMap.CharFor(e.Vk, shift, Layout)); return; }
            lock (Timeline) Shortcuts.Add("Key:" + e.Vk.ToString("X2"));
        }

        void DropSelection() { Screen.Length -= SelectedLen; SelectedLen = 0; }

        void Put(char c)
        {
            lock (Timeline) { if (SelectedLen > 0) DropSelection(); Screen.Append(c); }
            Record();
        }
    }

    /// <summary>The simulated machine: windows, keyboard state, the hook chain and the input stream.</summary>
    class FakeOS
    {
        public static FakeOS Cur;
        public static readonly IntPtr HklEn = new IntPtr(0x04090409), HklHe = new IntPtr(0x040D040D);
        readonly Stopwatch clock = Stopwatch.StartNew();
        public double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        public readonly List<FakeWindow> Windows = new List<FakeWindow>();
        public volatile FakeWindow Foreground;
        public volatile string ClipboardText;
        public volatile bool FocusedIsPassword;
        public double ProbeCostMs = 8;     // how long a UI Automation "is this a password box?" query takes
        public Action OnProbe;             // lets a scenario change something while the engine is mid-check
        public int Probes;
        public readonly List<double> KeyDowns = new List<double>(); // times of physical key presses

        readonly object stream = new object(); // the hardware input stream: one physical key or one SendInput batch at a time
        readonly Dictionary<int, bool> keys = new Dictionary<int, bool>();
        Native.HookProc kb, ms;
        class HookCall { public bool Quit, Mouse; public int Msg; public Native.KBDLLHOOKSTRUCT Kb; public bool Swallowed; public ManualResetEventSlim Done = new ManualResetEventSlim(); }
        readonly BlockingCollection<HookCall> hookCalls = new BlockingCollection<HookCall>();

        public FakeWindow AddWindow(Lang layout, string text = "")
        {
            var w = new FakeWindow(this, Windows.Count + 1, layout, text);
            Windows.Add(w);
            if (Foreground == null) Foreground = w;
            return w;
        }

        public static void BusyWait(double ms)
        {
            if (ms <= 0) return;
            long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
            if (ms > 3) Thread.Sleep((int)(ms - 2));
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
        }

        public static Lang LangOf(IntPtr hkl)
        {
            int primary = (int)(hkl.ToInt64() & 0x3FF);
            return primary == 0x0D ? Lang.He : primary == 0x09 ? Lang.En : Lang.Other;
        }

        FakeWindow ByHandle(IntPtr h) { foreach (var w in Windows) if (w.Hwnd == h || w.Focus == h) return w; return null; }

        // ---- physical keyboard / mouse ------------------------------------------------------------

        public void Phys(int vk, bool down, bool extended = false)
        {
            lock (stream)
            {
                if (down) lock (KeyDowns) KeyDowns.Add(Now);
                bool alt = IsDown(0x12);
                int msg = down ? (alt ? Native.WM_SYSKEYDOWN : Native.WM_KEYDOWN) : (alt ? Native.WM_SYSKEYUP : Native.WM_KEYUP);
                uint flags = (down ? 0 : Native.LLKHF_UP) | (extended ? 1u : 0u);
                Route(msg, vk, (uint)vk, flags, IntPtr.Zero, !down, false, '\0');
            }
        }

        public void Click()
        {
            if (ms == null) return;
            var c = new HookCall { Mouse = true, Msg = Native.WM_LBUTTONDOWN };
            hookCalls.Add(c); c.Done.Wait(5000);
        }

        void Route(int msg, int vk, uint scan, uint flags, IntPtr extra, bool up, bool unicode, char ch)
        {
            bool swallowed = false;
            if (kb != null)
            {
                var c = new HookCall { Msg = msg, Kb = new Native.KBDLLHOOKSTRUCT { vkCode = (uint)vk, scanCode = scan, flags = flags, dwExtraInfo = extra } };
                hookCalls.Add(c);
                if (!c.Done.Wait(5000)) throw new Exception("keyboard hook did not return (deadlock?)");
                swallowed = c.Swallowed;
            }
            if (swallowed) return; // a swallowed key never reaches the key state or the app
            if (!unicode) SetKey(vk, !up);
            var w = Foreground;
            if (w != null) w.Key(vk, up, unicode, ch);
        }

        void SetKey(int vk, bool down)
        {
            lock (keys)
            {
                keys[vk] = down;
                if (vk == 0xA0 || vk == 0xA1) keys[0x10] = Get(0xA0) || Get(0xA1);
                if (vk == 0xA2 || vk == 0xA3) keys[0x11] = Get(0xA2) || Get(0xA3);
                if (vk == 0xA4 || vk == 0xA5) keys[0x12] = Get(0xA4) || Get(0xA5);
            }
        }
        bool Get(int vk) { bool d; return keys.TryGetValue(vk, out d) && d; }
        public bool IsDown(int vk) { lock (keys) return Get(vk); }

        // ---- what Native forwards to ---------------------------------------------------------------

        public IntPtr SetHook(int id, Native.HookProc fn)
        {
            if (id == Native.WH_KEYBOARD_LL) { kb = fn; return new IntPtr(71); }
            ms = fn; return new IntPtr(72);
        }

        public void Unhook(IntPtr h) { if (h == new IntPtr(71)) kb = null; else if (h == new IntPtr(72)) ms = null; }

        /// <summary>Runs on the engine's hook thread: serves hook calls until WM_QUIT, like a real message pump would.</summary>
        public int GetMessage()
        {
            while (true)
            {
                var c = hookCalls.Take();
                if (c.Quit) return 0;
                if (c.Mouse) { var m = ms; if (m != null) m(0, new IntPtr(c.Msg), IntPtr.Zero); c.Done.Set(); continue; }
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.KBDLLHOOKSTRUCT)));
                try
                {
                    Marshal.StructureToPtr(c.Kb, p, false);
                    var k = kb;
                    c.Swallowed = k != null && k(0, new IntPtr(c.Msg), p) != IntPtr.Zero;
                }
                finally { Marshal.FreeHGlobal(p); c.Done.Set(); }
            }
        }

        public void Quit() { hookCalls.Add(new HookCall { Quit = true }); }

        public uint SendInput(Native.INPUT[] inputs)
        {
            lock (stream)
            {
                foreach (var i in inputs)
                {
                    var ki = i.U.ki;
                    bool up = (ki.dwFlags & Native.KEYEVENTF_KEYUP) != 0, uni = (ki.dwFlags & Native.KEYEVENTF_UNICODE) != 0;
                    bool ext = (ki.dwFlags & Native.KEYEVENTF_EXTENDEDKEY) != 0;
                    int vk = uni ? 0xE7 : ki.wVk;
                    bool alt = IsDown(0x12);
                    int msg = up ? (alt ? Native.WM_SYSKEYUP : Native.WM_KEYUP) : (alt ? Native.WM_SYSKEYDOWN : Native.WM_KEYDOWN);
                    uint flags = Native.LLKHF_INJECTED | (up ? Native.LLKHF_UP : 0) | (ext ? 1u : 0u);
                    Route(msg, vk, ki.wScan, flags, ki.dwExtraInfo, up, uni, (char)ki.wScan);
                }
            }
            return (uint)inputs.Length;
        }

        public IntPtr LayoutOfThread(uint tid)
        {
            foreach (var w in Windows)
            {
                if (tid != w.Tid && tid != w.FocusTid) continue;
                bool stale = w.StaleQuery || (w.SplitThreads && tid == w.Tid);
                return (stale ? w.InitialLayout : w.Layout) == Lang.He ? HklHe : HklEn;
            }
            return HklEn;
        }

        public uint ThreadOf(IntPtr hwnd, out uint pid)
        {
            var w = ByHandle(hwnd);
            if (w == null) { pid = 0; return 0; }
            pid = w.Pid;
            return hwnd == w.Focus ? w.FocusTid : w.Tid;
        }

        public bool GuiThreadInfo(uint tid, ref Native.GUITHREADINFO gi)
        {
            foreach (var w in Windows)
                if (tid == w.Tid || tid == w.FocusTid) { gi.hwndActive = w.Hwnd; gi.hwndFocus = w.Focus; return true; }
            return false;
        }

        public bool Post(IntPtr hwnd, uint msg, IntPtr l)
        {
            var w = ByHandle(hwnd);
            if (w == null || msg != Native.WM_INPUTLANGCHANGEREQUEST) return false;
            w.PostLayoutRequest(l);
            return true;
        }

        public string ExeOf(uint pid) { foreach (var w in Windows) if (w.Pid == pid) return "C:\\Apps\\" + w.Exe; return null; }
        public string TitleOf(IntPtr hwnd) { var w = ByHandle(hwnd); return w == null ? "" : w.Title; }

        /// <summary>Wait until nothing has been typed or changed on screen for a while.</summary>
        public void WaitIdle(int quietMs)
        {
            while (true)
            {
                double last = 0;
                lock (KeyDowns) if (KeyDowns.Count > 0) last = KeyDowns[KeyDowns.Count - 1];
                bool pending = false;
                foreach (var w in Windows) { last = Math.Max(last, w.LastChange); pending |= w.Pending > 0; }
                if (!pending && Now - last >= quietMs) return;
                Thread.Sleep(20);
            }
        }

        public void Close() { foreach (var w in Windows) w.Close(); }
    }

    /// <summary>Same surface as src/Native.cs, forwarded to the simulated machine.</summary>
    static class Native
    {
        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B;
        public const int WM_INPUTLANGCHANGEREQUEST = 0x50, WM_QUIT = 0x12;
        public const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
        public const int GWL_STYLE = -16, ES_PASSWORD = 0x20;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        static FakeOS Os { get { return FakeOS.Cur; } }

        public static IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId) { return Os.SetHook(idHook, fn); }
        public static bool UnhookWindowsHookEx(IntPtr hhk) { Os.Unhook(hhk); return true; }
        public static IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam) { return IntPtr.Zero; }
        public static IntPtr GetModuleHandle(string name) { return new IntPtr(1); }
        public static uint GetCurrentThreadId() { return (uint)Environment.CurrentManagedThreadId; }

        public static int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max) { msg = new MSG(); return Os.GetMessage(); }
        public static bool TranslateMessage(ref MSG msg) { return true; }
        public static IntPtr DispatchMessage(ref MSG msg) { return IntPtr.Zero; }
        public static bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l) { if (msg == WM_QUIT) Os.Quit(); return true; }

        public static uint SendInput(uint n, INPUT[] inputs, int size) { return Os.SendInput(inputs); }
        public static uint MapVirtualKey(uint code, uint mapType) { return code; }
        public static short GetAsyncKeyState(int vk) { return Os.IsDown(vk) ? unchecked((short)0x8000) : (short)0; }
        public static short GetKeyState(int vk) { return 0; }

        public static IntPtr GetForegroundWindow() { var w = Os.Foreground; return w == null ? IntPtr.Zero : w.Hwnd; }
        public static uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid) { return Os.ThreadOf(hwnd, out pid); }
        public static bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info) { return Os.GuiThreadInfo(threadId, ref info); }
        public static int GetWindowLong(IntPtr hwnd, int index) { return 0; }
        public static IntPtr GetKeyboardLayout(uint threadId) { return Os.LayoutOfThread(threadId); }
        public static int GetKeyboardLayoutList(int n, IntPtr[] list)
        {
            if (list != null && n >= 2) { list[0] = FakeOS.HklEn; list[1] = FakeOS.HklHe; }
            return 2;
        }
        public static int GetWindowText(IntPtr hwnd, StringBuilder text, int max) { text.Append(Os.TitleOf(hwnd)); return text.Length; }
        public static bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l) { return Os.Post(hwnd, msg, l); }

        public static IntPtr OpenProcess(uint access, bool inherit, uint pid) { return new IntPtr(pid); }
        public static bool CloseHandle(IntPtr h) { return true; }
        public static bool OpenProcessToken(IntPtr process, uint access, out IntPtr token) { token = new IntPtr(5); return true; }
        public static bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size)
        {
            string exe = Os.ExeOf((uint)process.ToInt64());
            if (exe == null) return false;
            name.Append(exe); size = exe.Length; return true;
        }

        public static uint timeBeginPeriod(uint ms) { return 0; }
        public static uint timeEndPeriod(uint ms) { return 0; }
    }
}

// ---- minimal stand-ins for the Windows-only framework types the engine and settings reference ----------------

namespace System.Windows.Automation
{
    public class AutomationProperty { }

    public class AutomationElement
    {
        public static readonly AutomationProperty IsPasswordProperty = new AutomationProperty();
        bool password;
        public static AutomationElement RootElement { get { return new AutomationElement(); } }
        public static AutomationElement FocusedElement
        {
            get
            {
                var os = KeyFlip.FakeOS.Cur;
                System.Threading.Interlocked.Increment(ref os.Probes);
                KeyFlip.FakeOS.BusyWait(os.ProbeCostMs);
                var hook = os.OnProbe; if (hook != null) hook();
                return new AutomationElement { password = os.FocusedIsPassword };
            }
        }
        public object GetCurrentPropertyValue(AutomationProperty property) { return password; }
    }
}

namespace System.Windows.Forms
{
    public enum Keys { None = 0, Pause = 0x13, Space = 0x20, F8 = 0x77, F9 = 0x78 }

    public interface IDataObject
    {
        string[] GetFormats(bool autoConvert);
        object GetData(string format, bool autoConvert);
    }

    public class DataObject : IDataObject
    {
        readonly System.Collections.Generic.Dictionary<string, object> data = new System.Collections.Generic.Dictionary<string, object>();
        public void SetData(string format, bool autoConvert, object value) { data[format] = value; }
        public string[] GetFormats(bool autoConvert) { return new System.Collections.Generic.List<string>(data.Keys).ToArray(); }
        public object GetData(string format, bool autoConvert) { object o; return data.TryGetValue(format, out o) ? o : null; }
    }

    public static class Clipboard
    {
        static KeyFlip.FakeOS Os { get { return KeyFlip.FakeOS.Cur; } }
        public static IDataObject GetDataObject()
        {
            var d = new DataObject();
            if (Os.ClipboardText != null) d.SetData("Text", false, Os.ClipboardText);
            return d;
        }
        public static void Clear() { Os.ClipboardText = null; }
        public static bool ContainsText() { return Os.ClipboardText != null; }
        public static string GetText() { return Os.ClipboardText; }
        public static void SetDataObject(object data, bool copy)
        {
            var d = data as IDataObject;
            Os.ClipboardText = d == null ? null : d.GetData("Text", false) as string;
        }
    }
}
