// KeyFlip - core logic (platform-neutral, no Windows calls here).
// Key mapping, word scoring, and the "run tracker" that decides when to fix text.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KeyFlip
{
    public enum Lang { En, He, Other }

    public enum UnitKind { Word, Space, Neutral }

    /// <summary>One typed key that produced one character on screen.</summary>
    public class Unit
    {
        public UnitKind Kind;
        public int Vk;
        public bool Shift;
        public Lang TypedIn;

        public Unit(UnitKind kind, int vk, bool shift, Lang typedIn)
        { Kind = kind; Vk = vk; Shift = shift; TypedIn = typedIn; }

        public char CharIn(Lang lang)
        {
            if (Kind == UnitKind.Space) return ' ';
            return KeyMap.CharFor(Vk, Shift, lang);
        }

        public Unit As(Lang lang) { return new Unit(Kind, Vk, Shift, lang); }
    }

    public static class KeyMap
    {
        // Virtual-key codes
        public const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20;
        public const int VK_OEM_1 = 0xBA, VK_OEM_PLUS = 0xBB, VK_OEM_COMMA = 0xBC, VK_OEM_MINUS = 0xBD,
                         VK_OEM_PERIOD = 0xBE, VK_OEM_2 = 0xBF, VK_OEM_3 = 0xC0, VK_OEM_4 = 0xDB,
                         VK_OEM_5 = 0xDC, VK_OEM_6 = 0xDD, VK_OEM_7 = 0xDE, VK_OEM_102 = 0xE2;

        // Unshifted characters per key: English (US) and Hebrew (Windows standard layout).
        static readonly Dictionary<int, char> En = new Dictionary<int, char>();
        static readonly Dictionary<int, char> He = new Dictionary<int, char>();
        static readonly Dictionary<int, char> ShiftedSymbol = new Dictionary<int, char>();
        static readonly Dictionary<char, int> EnRev = new Dictionary<char, int>();
        static readonly Dictionary<char, int> HeRev = new Dictionary<char, int>();

        static KeyMap()
        {
            string letters = "abcdefghijklmnopqrstuvwxyz";
            // a=ש b=נ c=ב d=ג e=ק f=כ g=ע h=י i=ן j=ח k=ל l=ך m=צ n=מ o=ם p=פ q=/ r=ר s=ד t=א u=ו v=ה w=' x=ס y=ט z=ז
            string heByLetter = "שנבגקכעיןחלךצמםפ/רדאוה'סטז";
            for (int i = 0; i < 26; i++)
            {
                int vk = 'A' + i;
                En[vk] = letters[i];
                He[vk] = heByLetter[i];
            }
            En[VK_OEM_1] = ';'; He[VK_OEM_1] = 'ף';
            En[VK_OEM_7] = '\''; He[VK_OEM_7] = ',';
            En[VK_OEM_COMMA] = ','; He[VK_OEM_COMMA] = 'ת';
            En[VK_OEM_PERIOD] = '.'; He[VK_OEM_PERIOD] = 'ץ';
            En[VK_OEM_2] = '/'; He[VK_OEM_2] = '.';
            En[VK_OEM_3] = '`'; He[VK_OEM_3] = ';';
            En[VK_OEM_4] = '['; He[VK_OEM_4] = ']';
            En[VK_OEM_6] = ']'; He[VK_OEM_6] = '[';
            En[VK_OEM_5] = '\\'; He[VK_OEM_5] = '\\';
            En[VK_OEM_MINUS] = '-'; He[VK_OEM_MINUS] = '-';
            En[VK_OEM_PLUS] = '='; He[VK_OEM_PLUS] = '=';
            for (int d = 0; d <= 9; d++) { En['0' + d] = (char)('0' + d); He['0' + d] = (char)('0' + d); }

            string shiftedDigits = ")!@#$%^&*(";
            for (int d = 0; d <= 9; d++) ShiftedSymbol['0' + d] = shiftedDigits[d];
            ShiftedSymbol[VK_OEM_1] = ':'; ShiftedSymbol[VK_OEM_7] = '"'; ShiftedSymbol[VK_OEM_COMMA] = '<';
            ShiftedSymbol[VK_OEM_PERIOD] = '>'; ShiftedSymbol[VK_OEM_2] = '?'; ShiftedSymbol[VK_OEM_3] = '~';
            ShiftedSymbol[VK_OEM_4] = '{'; ShiftedSymbol[VK_OEM_6] = '}'; ShiftedSymbol[VK_OEM_5] = '|';
            ShiftedSymbol[VK_OEM_MINUS] = '_'; ShiftedSymbol[VK_OEM_PLUS] = '+';

            foreach (var kv in En) if (!EnRev.ContainsKey(kv.Value)) EnRev[kv.Value] = kv.Key;
            foreach (var kv in He) if (!HeRev.ContainsKey(kv.Value)) HeRev[kv.Value] = kv.Key;
        }

        public static bool IsLetterKey(int vk) { return vk >= 'A' && vk <= 'Z'; }

        /// <summary>Keys that are part of a word in at least one of the two layouts.</summary>
        public static bool IsWordKey(int vk, bool shift)
        {
            if (IsLetterKey(vk)) return true;
            if (shift) return vk == VK_OEM_7; // '"' (gershayim inside Hebrew acronyms like דו"ח, or English quotes)
            return vk == VK_OEM_1 || vk == VK_OEM_7 || vk == VK_OEM_COMMA || vk == VK_OEM_PERIOD;
        }

        /// <summary>Printable keys we track (anything that types one character).</summary>
        public static bool IsPrintable(int vk)
        {
            return En.ContainsKey(vk) || (vk >= 0x60 && vk <= 0x6F && vk != 0x6C); // numpad digits/operators
        }

        public static char CharFor(int vk, bool shift, Lang lang)
        {
            if (IsLetterKey(vk) && shift) return (char)vk; // Shift+letter gives a Latin capital in both layouts
            if (shift && ShiftedSymbol.ContainsKey(vk)) return ShiftedSymbol[vk];
            if (vk >= 0x60 && vk <= 0x69) return (char)('0' + vk - 0x60);
            char c;
            if (lang == Lang.He) { if (He.TryGetValue(vk, out c)) return c; }
            else { if (En.TryGetValue(vk, out c)) return c; }
            return '?';
        }

        public static bool IsHebrewLetter(char c) { return c >= 'א' && c <= 'ת'; }
        public static bool IsFinalForm(char c) { return c == 'ך' || c == 'ם' || c == 'ן' || c == 'ף' || c == 'ץ'; }
        public static bool IsLatin(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

        /// <summary>Converts text as if the same keys had been pressed in the other layout.</summary>
        public static string ConvertText(string text, Lang from, Lang to)
        {
            var src = from == Lang.He ? HeRev : EnRev;
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                char lookup = (from == Lang.En && ch >= 'A' && ch <= 'Z') ? char.ToLowerInvariant(ch) : ch;
                int vk;
                if (src.TryGetValue(lookup, out vk) && IsLatinOrHebrewMapped(vk))
                    sb.Append(CharFor(vk, false, to));
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        static bool IsLatinOrHebrewMapped(int vk)
        {
            // Only remap keys whose meaning differs between layouts; digits and shared symbols stay as they are.
            return IsLetterKey(vk) || vk == VK_OEM_1 || vk == VK_OEM_7 || vk == VK_OEM_COMMA ||
                   vk == VK_OEM_PERIOD || vk == VK_OEM_2 || vk == VK_OEM_3;
        }

        /// <summary>Guess the language a piece of text was typed in (for selection conversion).</summary>
        public static Lang DetectScript(string text)
        {
            int he = 0, en = 0;
            foreach (char c in text) { if (IsHebrewLetter(c)) he++; else if (IsLatin(c)) en++; }
            if (he == 0 && en == 0) return Lang.Other;
            return he >= en ? Lang.He : Lang.En;
        }

        /// <summary>Reverse lookup: which key types this char in this layout (for tests / simulation).</summary>
        public static bool KeyFor(char c, Lang lang, out int vk, out bool shift)
        {
            shift = false;
            if (c >= 'A' && c <= 'Z') { vk = c; shift = true; return true; }
            var rev = lang == Lang.He ? HeRev : EnRev;
            if (rev.TryGetValue(c, out vk)) return true;
            foreach (var kv in ShiftedSymbol) if (kv.Value == c) { vk = kv.Key; shift = true; return true; }
            vk = 0; return false;
        }
    }

    /// <summary>Word-frequency dictionaries for Hebrew and English, plus the user's own word list.</summary>
    public class Lexicon
    {
        readonly Dictionary<string, double> en = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly Dictionary<string, double> he = new Dictionary<string, double>(StringComparer.Ordinal);
        public readonly HashSet<string> UserWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static readonly string[] Contractions = {
            "don't","can't","won't","isn't","aren't","wasn't","weren't","doesn't","didn't","haven't","hasn't","hadn't",
            "couldn't","wouldn't","shouldn't","i'm","i've","i'll","i'd","you're","you've","you'll","you'd","it's",
            "that's","there's","what's","let's","he's","she's","we're","we've","we'll","they're","they've","they'll" };

        public static Lexicon Load(Stream enList, Stream heList)
        {
            var lx = new Lexicon();
            Fill(lx.en, enList);
            Fill(lx.he, heList);
            foreach (var c in Contractions) if (!lx.en.ContainsKey(c)) lx.en[c] = 50;
            return lx;
        }

        static void Fill(Dictionary<string, double> d, Stream s)
        {
            var rows = new List<KeyValuePair<string, long>>();
            long total = 0;
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    int sp = line.LastIndexOf(' ');
                    if (sp <= 0) continue;
                    long n;
                    if (!long.TryParse(line.Substring(sp + 1), out n)) continue;
                    rows.Add(new KeyValuePair<string, long>(line.Substring(0, sp), n));
                    total += n;
                }
            }
            foreach (var kv in rows) d[kv.Key] = kv.Value * 1e6 / total; // occurrences per million words
        }

        /// <summary>How common this text is as a word in the given language (per million words). 0 = not a word.</summary>
        public double Score(string word, Lang lang)
        {
            if (string.IsNullOrEmpty(word)) return 0;
            if (lang == Lang.En)
            {
                foreach (char c in word) if (!KeyMap.IsLatin(c) && c != '\'') return 0;
                if (word[0] == '\'' || word[word.Length - 1] == '\'') return 0;
                string w = word.ToLowerInvariant();
                double v;
                return en.TryGetValue(w, out v) ? v : 0;
            }
            else
            {
                for (int i = 0; i < word.Length; i++)
                {
                    char c = word[i];
                    if (KeyMap.IsHebrewLetter(c))
                    {
                        if (KeyMap.IsFinalForm(c) && i < word.Length - 1 && word[i + 1] != '\'') return 0; // final letter mid-word
                        continue;
                    }
                    if ((c == '\'' || c == '"') && i > 0) continue; // geresh / gershayim (צ'יפס, דו"ח)
                    return 0;
                }
                double v;
                if (he.TryGetValue(word, out v)) return v;
                string plain = word.Replace("\"", "").Replace("'", "");
                if (plain != word && he.TryGetValue(plain, out v)) return v;
                // Hebrew prefixes: ו ה ב ל מ ש כ (e.g. "והמייל", "שבדוח")
                for (int p = 1; p <= 3 && word.Length - p >= 2; p++)
                {
                    if ("והבלמשכ".IndexOf(word[p - 1]) < 0) break;
                    if (he.TryGetValue(word.Substring(p), out v)) return v * 0.2;
                }
                return 0;
            }
        }
    }

    public class Settings
    {
        public bool AutoFix = true;
        public bool Lookback = true;
        public int MinWordLength = 2;
        public double MinScore = 1.0;     // per million: minimum for the "intended" word to count as real
        public double MinScoreShort = 20; // stricter for 2-letter words
        public double Ratio = 20;         // intended word must be this much more common than what was typed
        public double KeepIfTypedAbove = 10; // never auto-flip a word that is itself this common (per million)
    }

    public class FixPlan
    {
        public string Kind;            // "auto", "manual", "undo"
        public int Backspaces;
        public Lang SwitchTo;
        public List<Unit> Retype = new List<Unit>(); // units already converted to SwitchTo
        public Unit Boundary;          // key to re-send at the end (space, punctuation, enter...), may be null
        public string Before, After;   // for log / tooltip
        public string LearnWord;       // undo: remember this so it isn't fixed again
    }

    /// <summary>
    /// Tracks what was typed in the current window since the last click/navigation and decides on fixes.
    /// All methods are called from the keyboard-hook thread.
    /// </summary>
    public class RunTracker
    {
        public const int MaxRun = 400;
        readonly List<Unit> run = new List<Unit>();
        readonly Lexicon lex;
        public Settings Settings;

        // last auto-fix, for undo
        List<Unit> undoOriginal;   // units as typed (wrong layout)
        int undoStart = -1, undoLen;

        public RunTracker(Lexicon lex, Settings settings) { this.lex = lex; Settings = settings; }

        public int Count { get { return run.Count; } }
        public string Text(Lang? forceLang = null)
        {
            var sb = new StringBuilder();
            foreach (var u in run) sb.Append(u.CharIn(forceLang ?? u.TypedIn));
            return sb.ToString();
        }

        public void Reset() { run.Clear(); undoStart = -1; undoOriginal = null; }

        public void Backspace()
        {
            if (run.Count > 0) run.RemoveAt(run.Count - 1);
            undoStart = -1; undoOriginal = null;
        }

        static Lang Other(Lang l) { return l == Lang.He ? Lang.En : Lang.He; }

        /// <summary>A word or space key was typed (not a boundary).</summary>
        public void AddWordUnit(Unit u)
        {
            if (undoStart >= 0) { undoStart = -1; undoOriginal = null; }
            run.Add(u);
            if (run.Count > MaxRun) run.RemoveRange(0, run.Count - MaxRun);
        }

        /// <summary>
        /// A boundary key was pressed (space, punctuation, enter, tab). Returns a plan if the word before it
        /// should be fixed (the caller then swallows the key and lets the plan re-send it), else null and the
        /// key is recorded normally.
        /// </summary>
        public FixPlan Boundary(Unit boundary, bool resetAfter, bool allowFix)
        {
            FixPlan plan = null;
            if (allowFix && Settings.AutoFix) plan = TryAutoFix(boundary);
            if (plan == null)
            {
                if (undoStart >= 0) { undoStart = -1; undoOriginal = null; }
                if (boundary != null) AddUnit(boundary);
            }
            if (resetAfter) { run.Clear(); undoStart = -1; undoOriginal = null; }
            return plan;
        }

        void AddUnit(Unit u)
        {
            run.Add(u);
            if (run.Count > MaxRun) { run.RemoveRange(0, run.Count - MaxRun); undoStart = -1; undoOriginal = null; }
        }

        /// <summary>Find [start,end) of the word ending at index end (exclusive).</summary>
        int WordStart(int end)
        {
            int i = end;
            while (i > 0 && run[i - 1].Kind == UnitKind.Word) i--;
            return i;
        }

        static string Join(List<Unit> units, int start, int end, Lang lang)
        {
            var sb = new StringBuilder();
            for (int i = start; i < end; i++) sb.Append(units[i].CharIn(lang));
            return sb.ToString();
        }

        static string StripTrailing(string s, Lang lang)
        {
            string punct = lang == Lang.En ? ",.;'\"" : ",'/\"";
            int b = 0, e = s.Length;
            while (b < e && s[b] == '"') b++;
            while (e > b && punct.IndexOf(s[e - 1]) >= 0) e--;
            return s.Substring(b, e - b);
        }

        public double ScoreIn(string raw, Lang lang)
        {
            string core = StripTrailing(raw, lang);
            if (core.Length == 0) return 0;
            if (core.Length == 1) return (core == "a" || core == "I" || core == "i") ? 100 : 0; // lone letters aren't evidence
            return lex.Score(core, lang);
        }

        bool IsUserWord(string a, string b)
        {
            return lex.UserWords.Contains(a) || lex.UserWords.Contains(b) ||
                   lex.UserWords.Contains(StripTrailing(a, Lang.En)) || lex.UserWords.Contains(StripTrailing(a, Lang.He)) ||
                   lex.UserWords.Contains(StripTrailing(b, Lang.En)) || lex.UserWords.Contains(StripTrailing(b, Lang.He));
        }

        /// <summary>Decide whether a word typed in layout L was really meant in the other layout.</summary>
        public bool ShouldFix(string typedText, string otherText, Lang typed, out double sTyped, out double sOther)
        {
            Lang other = Other(typed);
            sTyped = ScoreIn(typedText, typed);
            sOther = ScoreIn(otherText, other);
            int len = StripTrailing(otherText, other).Length;
            if (len < Settings.MinWordLength) return false;
            if (IsUserWord(typedText, otherText)) return false;
            double min = len <= 2 ? Settings.MinScoreShort : Settings.MinScore;
            if (sOther < min) return false;
            if (sTyped == 0) return true;
            if (sTyped >= Settings.KeepIfTypedAbove) return false;
            return sOther >= sTyped * Settings.Ratio;
        }

        FixPlan TryAutoFix(Unit boundary)
        {
            int end = run.Count;
            int start = WordStart(end);
            if (start == end) return null;
            Lang typed = run[end - 1].TypedIn;
            if (typed == Lang.Other) return null;
            for (int i = start; i < end; i++) if (run[i].TypedIn != typed) return null;
            Lang other = Other(typed);

            string tText = Join(run, start, end, typed);
            string oText = Join(run, start, end, other);
            double st, so;
            if (!ShouldFix(tText, oText, typed, out st, out so)) return null;

            // Look back over earlier words in the same run (separated by spaces) that also read better in the other language,
            // e.g. "do akuo" -> "גם שלום" ("do" is English too, but the next word shows Hebrew was meant).
            int fixStart = start;
            if (Settings.Lookback)
            {
                int words = 0;
                int p = start;
                while (words < 6)
                {
                    int q = p;
                    while (q > 0 && run[q - 1].Kind == UnitKind.Space) q--;
                    if (q == p || q == 0) break;               // no space before, or run starts with spaces only
                    int ws = WordStart(q);
                    if (ws == q) break;                        // hit a neutral char
                    bool sameLayout = true;
                    for (int i = ws; i < p; i++) if (run[i].TypedIn != typed) { sameLayout = false; break; }
                    if (!sameLayout) break;
                    string pt = Join(run, ws, q, typed), po = Join(run, ws, q, other);
                    double a = ScoreIn(pt, typed), b = ScoreIn(po, other);
                    if (IsUserWord(pt, po)) break;
                    // Convert if it reads at least as naturally in the intended language. The first word of a passage
                    // gets more benefit of the doubt: starting in the wrong layout is the typical mistake.
                    bool firstOfRun = true;
                    for (int i = 0; i < ws; i++) if (run[i].Kind != UnitKind.Space) { firstOfRun = false; break; }
                    bool convert = b >= Settings.MinScore && (b >= a || (firstOfRun && b * 10 >= a));
                    if (!convert) break;
                    fixStart = ws; p = ws; words++;
                }
            }

            var plan = new FixPlan { Kind = "auto", SwitchTo = other, Boundary = boundary };
            plan.Backspaces = end - fixStart;
            var original = new List<Unit>();
            for (int i = fixStart; i < end; i++) { original.Add(run[i]); plan.Retype.Add(run[i].As(other)); }
            plan.Before = Join(run, fixStart, end, typed);
            plan.After = Join(run, fixStart, end, other);

            // update state as if the plan succeeded
            run.RemoveRange(fixStart, end - fixStart);
            run.AddRange(plan.Retype);
            if (boundary != null) { boundary.TypedIn = other; AddUnit(boundary); }
            undoOriginal = original; undoStart = fixStart; undoLen = original.Count;
            if (boundary == null || boundary.Vk == KeyMap.VK_RETURN || boundary.Vk == KeyMap.VK_TAB) undoStart = -1;
            return plan;
        }

        /// <summary>The hotkey: undo the last auto-fix if nothing was typed since; otherwise flip the recent text.</summary>
        public FixPlan Manual()
        {
            // Undo: run must end with exactly the fixed units + the boundary.
            if (undoStart >= 0 && undoOriginal != null && run.Count == undoStart + undoLen + 1)
            {
                var boundary = run[run.Count - 1];
                Lang back = undoOriginal[0].TypedIn;
                var plan = new FixPlan { Kind = "undo", SwitchTo = back, Backspaces = undoLen + 1 };
                plan.Retype.AddRange(undoOriginal);
                plan.Boundary = boundary.As(back);
                plan.Before = Join(run, undoStart, undoStart + undoLen, run[undoStart].TypedIn);
                plan.After = Join(undoOriginal, 0, undoOriginal.Count, back);
                // learn the last word as typed so it is never "fixed" again
                int ws = undoOriginal.Count;
                while (ws > 0 && undoOriginal[ws - 1].Kind == UnitKind.Word) ws--;
                plan.LearnWord = StripTrailing(Join(undoOriginal, ws, undoOriginal.Count, back), back);
                run.RemoveRange(undoStart, run.Count - undoStart);
                run.AddRange(undoOriginal);
                run.Add(plan.Boundary);
                undoStart = -1; undoOriginal = null;
                return plan;
            }

            if (run.Count == 0) return null;
            // Flip the trailing part of the run that was typed in one layout.
            int end = run.Count;
            int e2 = end;
            while (e2 > 0 && run[e2 - 1].Kind == UnitKind.Space) e2--; // ignore trailing spaces when choosing layout
            if (e2 == 0) return null;
            Lang typed = run[e2 - 1].TypedIn;
            if (typed == Lang.Other) return null;
            int s = e2;
            while (s > 0 && (run[s - 1].TypedIn == typed || run[s - 1].Kind == UnitKind.Space)) s--;
            while (s < e2 && run[s].Kind == UnitKind.Space) s++; // don't start on a space
            Lang other = Other(typed);
            var mp = new FixPlan { Kind = "manual", SwitchTo = other, Backspaces = end - s };
            for (int i = s; i < end; i++) mp.Retype.Add(run[i].As(other));
            mp.Before = Join(run, s, end, run[Math.Max(s, 0)].TypedIn);
            mp.After = Join(mp.Retype, 0, mp.Retype.Count, other);
            run.RemoveRange(s, end - s);
            run.AddRange(mp.Retype);
            undoStart = -1; undoOriginal = null;
            return mp;
        }
    }
}
