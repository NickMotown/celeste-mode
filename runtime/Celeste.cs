// Celeste mode : one hit is a death, a death
// takes the game back `rewind` seconds, deaths are counted upward in place of
// the lives. A watcher beside RetroArch, talking to it with RetroArch's
// network commands on 127.0.0.1 (nothing leaves the PC): it reads memory,
// takes snapshots (save states it copies aside), loads one back on a hit,
// writes the lives and shows the count.
//
// Compiled by launch.ps1 with Add-Type, i.e. Windows PowerShell 5.1's C# 5
// compiler: no string interpolation, no expression-bodied members, no ?. .
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Celeste
{
    // ------------------------------------------------------------------ log --
    // One JSON object per line (logging.md). Values are numbers or strings.
    public static class Log
    {
        public static string File;
        static readonly object gate = new object();  // the pad threads write too

        public static void Write(string evt, params object[] kv)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ts\":\"").Append(DateTime.Now.ToString("o")).Append("\",\"event\":\"").Append(Esc(evt)).Append('"');
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                sb.Append(",\"").Append(Esc(Convert.ToString(kv[i], CultureInfo.InvariantCulture))).Append("\":");
                object v = kv[i + 1];
                // NaN and infinity are not JSON numbers: quoted (a rewind
                // without a limit is infinite, 2026-10-09)
                if (v is double && (double.IsNaN((double)v) || double.IsInfinity((double)v))) sb.Append('"').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('"');
                else if (v is int || v is long || v is double) sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                else if (v is bool) sb.Append((bool)v ? "true" : "false");
                else sb.Append('"').Append(Esc(Convert.ToString(v, CultureInfo.InvariantCulture))).Append('"');
            }
            sb.Append("}\n");
            lock (gate)
            {
                if (File != null) System.IO.File.AppendAllText(File, sb.ToString(), new UTF8Encoding(false));
                Console.Error.Write(sb.ToString());
            }
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(string.Format("\\u{0:x4}", (int)c));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------ rule file --
    // A strict subset of YAML: `key: value`, and `  - item` lines under a key
    // with no value; # comments and blank lines skipped. A list item may be a
    // map (`checkpoints:`): `  - name: …` then `    when: …` lines
    // indented deeper than its dash. A key with no value may instead hold
    // indented `key: value` lines, a map (`variables:`, keys addresses).
    // Anything else is an error naming the line: a rule file that
    // half-parses would play wrong. A list holds strings and
    // Dictionary<string, string> maps.
    // `variables:`: names for addresses, given
    // once at the top of a rule file, then used in the conditions and the
    // address keys (`Stage == 0x05 && Screen changed`, `lives: Lives`).
    // Resolved to the addresses before anything else reads the file;
    // `keep` ranges stay addresses.
    public static class Variables
    {
        static readonly string[] Words = { "changed", "unchanged", "decreased", "increased", "by", "was", "set", "to" };
        static readonly string[] Keys = { "hit", "barrier", "lives", "input", "screen", "song" };
        static readonly string[] MapFields = { "when", "patch" };

        public static void Resolve(Dictionary<string, object> r)
        {
            if (!r.ContainsKey("variables")) return;
            var given = r["variables"] as Dictionary<string, string>;
            if (given == null) throw new FormatException("rule file: 'variables' holds indented '0x…: Name' lines");
            r.Remove("variables");
            var names = new Dictionary<string, string>();
            var seen = new HashSet<int>();
            foreach (KeyValuePair<string, string> kv in given)
            {
                int a = Conditions.Number(kv.Key, "variable address", "variables: " + kv.Key);
                string name = kv.Value;
                if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new FormatException("rule file: variable name '" + name + "' (" + kv.Key + "): letters, digits and _, starting with a letter");
                if (Array.IndexOf(Words, name.ToLowerInvariant()) >= 0) throw new FormatException("rule file: variable name '" + name + "' is a condition word");
                if (names.ContainsKey(name)) throw new FormatException("rule file: variable name '" + name + "' given twice");
                if (!seen.Add(a)) throw new FormatException("rule file: address " + kv.Key + " named twice");
                names[name] = "0x" + a.ToString("X4", CultureInfo.InvariantCulture);
            }
            foreach (string key in Keys)
            {
                if (!r.ContainsKey(key)) continue;
                string one = r[key] as string;
                if (one != null) { r[key] = Sub(one, names); continue; }
                var list = r[key] as List<object>;
                if (list != null) for (int i = 0; i < list.Count; i++) if (list[i] is string) list[i] = Sub((string)list[i], names);
            }
            foreach (string key in new[] { "checkpoints", "patches" })
            {
                var list = r.ContainsKey(key) ? r[key] as List<object> : null;
                if (list == null) continue;
                foreach (object o in list)
                {
                    var m = o as Dictionary<string, string>;
                    if (m == null) continue;
                    foreach (string f in MapFields) if (m.ContainsKey(f)) m[f] = Sub(m[f], names);
                }
            }
        }

        // every name in s as its address; any other word as it is (a word
        // that is not a number then fails where the value is read)
        public static string Sub(string s, IDictionary<string, string> names)
        {
            return Regex.Replace(s, @"(?<![\w])[A-Za-z_]\w*(?![\w])", m => { string v; return names.TryGetValue(m.Value, out v) ? v : m.Value; });
        }
    }

    public static class RuleFile
    {
        public static Dictionary<string, object> Parse(string text)
        {
            var rules = new Dictionary<string, object>();
            string listKey = null;
            Dictionary<string, string> map = null;   // the map item being filled
            int mapIndent = -1;                       // its dash's column
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 1; n <= lines.Length; n++)
            {
                string raw = lines[n - 1];
                string line = StripComment(raw);
                if (line.Trim().Length == 0) continue;
                Match item = Regex.Match(line, @"^(\s+)-\s+(.+)$");
                Match kv = Regex.Match(line, @"^([A-Za-z_][\w-]*):\s*(.*?)\s*$");
                Match field = Regex.Match(line, @"^(\s+)([A-Za-z_0-9][\w-]*):\s*(.*?)\s*$");
                if (item.Success)
                {
                    if (listKey == null || !(rules[listKey] is List<object>)) throw new FormatException(string.Format("rule file line {0}: a list item outside a list: {1}", n, raw.Trim()));
                    string content = item.Groups[2].Value.Trim();
                    Match first = Regex.Match(content, @"^([A-Za-z_][\w-]*):\s*(.*?)\s*$");
                    if (first.Success)
                    {
                        map = new Dictionary<string, string>();
                        mapIndent = item.Groups[1].Value.Length;
                        map[first.Groups[1].Value] = Unquote(first.Groups[2].Value);
                        ((List<object>)rules[listKey]).Add(map);
                    }
                    else { map = null; ((List<object>)rules[listKey]).Add(Unquote(content)); }
                }
                else if (field.Success && map != null && field.Groups[1].Value.Length > mapIndent)
                {
                    string fk = field.Groups[2].Value;
                    if (map.ContainsKey(fk)) throw new FormatException(string.Format("rule file line {0}: '{1}' given twice in one item", n, fk));
                    map[fk] = Unquote(field.Groups[3].Value);
                }
                else if (field.Success && map == null && listKey != null && (rules[listKey] is Dictionary<string, string> || ((List<object>)rules[listKey]).Count == 0))
                {
                    var plain = rules[listKey] as Dictionary<string, string>;
                    if (plain == null) { plain = new Dictionary<string, string>(); rules[listKey] = plain; }
                    string pk = field.Groups[2].Value;
                    if (plain.ContainsKey(pk)) throw new FormatException(string.Format("rule file line {0}: '{1}' given twice under '{2}'", n, pk, listKey));
                    plain[pk] = Unquote(field.Groups[3].Value);
                }
                else if (kv.Success)
                {
                    map = null;
                    string key = kv.Groups[1].Value;
                    if (rules.ContainsKey(key)) throw new FormatException(string.Format("rule file line {0}: '{1}' given twice", n, key));
                    if (kv.Groups[2].Value.Length == 0) { rules[key] = new List<object>(); listKey = key; }
                    else { rules[key] = Unquote(kv.Groups[2].Value); listKey = null; }
                }
                else throw new FormatException(string.Format("rule file line {0}: not 'key: value' nor '  - item' nor an item's '    key: value': {1}", n, raw.Trim()));
            }
            return rules;
        }

        static string StripComment(string line)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '#') return line.Substring(0, i);
            }
            return line;
        }

        static string Unquote(string s)
        {
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0]) return s.Substring(1, s.Length - 2);
            return s;
        }
    }

    // ----------------------------------------------------------- conditions --
    // a playlist trigger syntax (0x0712==0x01, != < > <= >=,
    // &&) plus `<addr> changed|decreased|increased` and `<addr> was <value>`
    // against the previous read.
    public delegate bool Condition(IDictionary<int, int> cur, IDictionary<int, int> prev);

    public static class Conditions
    {
        public static int Number(string tok, string what, string src)
        {
            int v;
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(tok.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) && tok.Length > 2) return v;
            if (Regex.IsMatch(tok, @"^\d+$") && int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return v;
            throw new FormatException(string.Format("condition '{0}': {1} '{2}' is not a number (0x.. or decimal)", src, what, tok));
        }

        // steady: the addresses a clause reads as a level (decreased,
        // increased, unchanged, < >…): the first starting point waits until
        // they have held still. markers: { addr, value } of `== value`
        // clauses: a death marker ("0x0580 changed && 0x0580 == 0x41") may
        // move all the time (Mega Man 2's player flags, Ghosts'n Goblins'
        // pose); the starting point only waits while it shows its value.
        // "||" between alternatives, each a chain of "&&" clauses (&& binds
        // tighter): a stage's start can fire on its change or on play
        // beginning in it, the first stage of a game included (Ninja Gaiden
        // III's area 0 is the power-on value: no change ever started it).
        public static Condition Compile(string src, ISet<int> addrs, ISet<int> steady = null, IList<int[]> markers = null)
        {
            var alternatives = new List<List<Condition>>();
            foreach (string alt in src.Split(new[] { "||" }, StringSplitOptions.None))
            {
                var clauses = new List<Condition>();
                foreach (string part in alt.Split(new[] { "&&" }, StringSplitOptions.None)) clauses.Add(Clause(part.Trim(), src, addrs, steady, markers));
                alternatives.Add(clauses);
            }
            return delegate(IDictionary<int, int> cur, IDictionary<int, int> prev)
            {
                foreach (List<Condition> clauses in alternatives)
                {
                    bool all = true;
                    foreach (Condition c in clauses) if (!c(cur, prev)) { all = false; break; }
                    if (all) return true;
                }
                return false;
            };
        }

        static Condition Clause(string clause, string src, ISet<int> addrs, ISet<int> steady, IList<int[]> markers)
        {
            // "<addr> decreased by <n>": down by exactly n. A death takes one
            // life; Contra's title holds 0x62 in its lives byte and the game
            // start sets 0x02, a drop of 0x60 that is no death (2026-10-09).
            Match by = Regex.Match(clause, @"^(\S+)\s+decreased\s+by\s+(\S+)$");
            if (by.Success)
            {
                int a = Number(by.Groups[1].Value, "address", src);
                int n = Number(by.Groups[2].Value, "value", src);
                addrs.Add(a);
                if (steady != null) steady.Add(a);
                return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] - c[a] == n; };
            }
            // "<addr> was <value>": the previous read held that value. With
            // "&& <addr> != <value>" it is the moment a state ends: Mega Man
            // 2's READY ($0580 == 0x01) giving the player control, which no
            // single value marks (2026-10-10).
            // "<addr> was <op> <value>": the previous read compared so
            // (Mega Man 2's pit: the song before the death marker was a
            // stage's, below the boss theme's 0xAD; at a pit the song drops
            // one frame after the marker, too fast for a read to catch the
            // two together, 2026-10-11)
            Match was = Regex.Match(clause, @"^(\S+)\s+was\s+(?:(==|!=|<=|>=|<|>)\s*)?(\S+)$");
            if (was.Success)
            {
                int a = Number(was.Groups[1].Value, "address", src);
                int v = Number(was.Groups[3].Value, "value", src);
                string wop = was.Groups[2].Success ? was.Groups[2].Value : "==";
                addrs.Add(a);
                switch (wop)
                {
                    case "!=": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] != v; };
                    case "<": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] < v; };
                    case ">": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] > v; };
                    case "<=": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] <= v; };
                    case ">=": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] >= v; };
                    default: return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return p[a] == v; };
                }
            }
            Match t = Regex.Match(clause, @"^(\S+)\s+(changed|unchanged|decreased|increased)$");
            if (t.Success)
            {
                int a = Number(t.Groups[1].Value, "address", src);
                addrs.Add(a);
                string w = t.Groups[2].Value;
                if (w != "changed" && steady != null) steady.Add(a);
                if (w == "changed") return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] != p[a]; };
                // a two-byte counter's low byte drops on a hit, but also on a
                // carry when the high byte rises (Metroid's energy, 19 -> 20):
                // "<low> decreased && <high> unchanged" (2026-10-09)
                if (w == "unchanged") return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] == p[a]; };
                if (w == "decreased") return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] < p[a]; };
                return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] > p[a]; };
            }
            Match m = Regex.Match(clause, @"^(\S+?)\s*(==|!=|<=|>=|<|>)\s*(\S+)$");
            if (m.Success)
            {
                int a = Number(m.Groups[1].Value, "address", src);
                int v = Number(m.Groups[3].Value, "value", src);
                addrs.Add(a);
                // `!=` is a guard ("while a song still plays", Mega Man 2's
                // death marker), not a level: the song byte moves on the
                // menus, and as a level it kept the first starting point
                // from ever being taken (e2e, 2026-10-10)
                if (m.Groups[2].Value == "==") { if (markers != null) markers.Add(new[] { a, v }); }
                else if (m.Groups[2].Value != "!=" && steady != null) steady.Add(a);
                switch (m.Groups[2].Value)
                {
                    case "==": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] == v; };
                    case "!=": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] != v; };
                    case "<=": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] <= v; };
                    case ">=": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] >= v; };
                    case "<": return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] < v; };
                    default: return delegate(IDictionary<int, int> c, IDictionary<int, int> p) { return c[a] > v; };
                }
            }
            throw new FormatException(string.Format("condition '{0}': '{1}' is neither '<addr> <op> <value>' nor '<addr> changed|unchanged|decreased|increased' nor '<addr> decreased by <n>' nor '<addr> was <value>'", src, clause));
        }
    }

    // A rule file, checked and compiled: what the watcher needs.
    public class Game
    {
        public string Name = "?";
        public string Rom;
        // the ROM's system: the first folder of `rom:` (nes, snes)
        public string RomSystem { get { return Rom == null ? null : Rom.Split('/', '\\')[0]; } }
        public double Rewind = 10;
        public List<Condition> Hit = new List<Condition>();
        // `death:` (optional): what counts as a death besides the lives
        // going down, for a game whose lives are not a number (Darkwing
        // Duck's are display tiles): single hits off and free zones count
        // only deaths (2026-10-12)
        public List<Condition> Death = new List<Condition>();
        public SortedSet<int> DeathAddrs = new SortedSet<int>();

        // A death now: the lives went down, or a `death` condition holds.
        public bool IsDeath(IDictionary<int, int> cur, IDictionary<int, int> prev)
        {
            if (Lives >= 0 && cur[Lives] < prev[Lives]) return true;
            foreach (Condition c in Death) if (c(cur, prev)) return true;
            return false;
        }
        public int Lives = -1;
        // The lowest count written into the lives byte: 1, or 2 where the
        // byte's 1 is already the last life (Contra Force: HUD x00 at 1, its
        // next death a game over with no drop, 2026-10-09).
        public int LivesFloor = 1;
        public int Input = -1;                       // a pad byte: non-zero = a button held
        // `input_bits`: the NES buttons in `input`'s bits, bit 0 first (Mega
        // Man 1: "a b select start up down left right"; DuckTales 2 the
        // same, Ghosts'n Goblins the other way round): name -> mask
        public Dictionary<string, int> InputBits;
        public static readonly string[] NesButtons = { "a", "b", "select", "start", "up", "down", "left", "right" };
        // `patches`: bytes written on a button
        // press, from the start and kept, or both
        public List<PadPatch> Patches = new List<PadPatch>();
        public List<int[]> Keep = new List<int[]>(); // { first, last }: carried across a rewind (music)
        // The bytes naming the song playing: the music is carried across only
        // when they read the same now and in the snapshot (a death jingle, a
        // boss theme, carried into a stage, broke the music, 2026-10-09).
        public List<int> Song = new List<int>();
        // `song: "<addr> & <mask>"`: only the mask's bits name the song.
        // Mega Man 2's $055E flips AD/AE (9D/9E) inside one boss theme, and
        // each flip read as another song, restarting the music (2026-10-10).
        public Dictionary<int, int> SongMask = new Dictionary<int, int>();
        public int SongBits(int addr, int value) { int m; return SongMask.TryGetValue(addr, out m) ? value & m : value; }
        public SortedSet<int> Addrs = new SortedSet<int>();
        public SortedSet<int> HitAddrs = new SortedSet<int>(); // read by the hit conditions, and the lives
        // Of those, the levels (health, lives…) that must hold still before
        // the first starting point, and the death markers ({ addr, value })
        // it must not be taken on (Conditions.Compile).
        public SortedSet<int> SteadyAddrs = new SortedSet<int>();
        public List<int[]> Markers = new List<int[]>();
        // The screen byte (optional):
        // read only when the REWIND TO SCREEN option is on (Options), then a
        // change of its value makes the next snapshot a starting point and a
        // death goes back to it. -1: none, and the option is ignored.
        public int Screen = -1;
        // REWIND TO SCREEN's N (Options): how many starting points a hit goes
        // back; 0 = off.
        public int ScreenN;
        public bool ScreenOn { get { return ScreenN > 0; } }
        // REWIND TO SCREEN's hold past the floor (Rewinder.Hold, Options).
        public double Hold = 10;
        // `barrier:` (optional): conditions in the `hit:` syntax; when
        // one turns true the next starting point is a barrier, which a hit
        // never goes back past (a new level, a boss room). Read only while
        // REWIND TO SCREEN is in effect.
        public List<Condition> Barrier = new List<Condition>();
        public List<string> BarrierSrc = new List<string>();
        public SortedSet<int> BarrierAddrs = new SortedSet<int>();
        // COUNT LIVES (Options): false leaves the game's lives byte alone.
        public bool WriteLives = true;
        // `mode:` (both systems coexist, the file says which one it
        // uses): "rewind" (the default: REWIND TIME and REWIND
        // TO SCREEN with `screen`/`barrier`) or "checkpoints" (a hit goes
        // back to the last checkpoint of `checkpoints:`, Sequencer).
        public string Mode = "rewind";
        public bool UsesCheckpoints { get { return Mode == "checkpoints"; } }
        public List<CheckpointDef> Checkpoints = new List<CheckpointDef>();
        public SortedSet<int> CheckpointAddrs = new SortedSet<int>();
        // Seconds without a hit before a checkpoint counts (default 2; a
        // checkpoint's own `wait` wins).
        public double CheckpointWait = 2;
        // CHECKPOINTS (Options): the difficulty whose checkpoints are used.
        public string Level = "medium";
        public static readonly string[] Levels = { "easy", "medium", "hard" };

        static readonly string[] Known = { "name", "rom", "rewind", "hit", "death", "lives", "lives_floor", "input", "input_bits", "keep", "song", "screen", "barrier", "mode", "checkpoints", "checkpoint_wait", "patches" };
        static readonly string[] RewindOnly = { "screen", "barrier" };
        static readonly string[] CheckpointsOnly = { "checkpoints", "checkpoint_wait" };
        // Taken out, to keep rules simple: a byte
        // saying "in a stage" or "a new screen" is one more guess per game, and
        // Mega Man 5's was wrong in ways no blind test showed (a 400-death
        // loop, hits uncounted, play time stopped).
        static readonly string[] Removed = { "playing", "checkpoint" };

        public static Game FromText(string text)
        {
            Dictionary<string, object> r = RuleFile.Parse(text);
            Variables.Resolve(r);
            foreach (string k in r.Keys)
            {
                if (Array.IndexOf(Removed, k) >= 0) throw new FormatException("rule file: '" + k + "' is no longer read: a rule is only health and lives (see the README); remove it");
                if (Array.IndexOf(Known, k) < 0) throw new FormatException("rule file: unknown key '" + k + "'");
            }
            var g = new Game();
            if (r.ContainsKey("name")) g.Name = Scalar(r, "name");
            if (r.ContainsKey("rom")) g.Rom = Scalar(r, "rom");
            if (r.ContainsKey("rewind"))
            {
                double s;
                if (!double.TryParse(Scalar(r, "rewind"), NumberStyles.Float, CultureInfo.InvariantCulture, out s) || s <= 0) throw new FormatException("rule file: 'rewind' must be a number of seconds above 0");
                g.Rewind = s;
            }
            foreach (string src in List(r, "hit", true)) g.Hit.Add(Conditions.Compile(src, g.HitAddrs, g.SteadyAddrs, g.Markers));
            foreach (string src in List(r, "death", false)) g.Death.Add(Conditions.Compile(src, g.DeathAddrs));
            g.Addrs.UnionWith(g.HitAddrs);
            g.Addrs.UnionWith(g.DeathAddrs);
            if (r.ContainsKey("lives")) { g.Lives = Conditions.Number(Scalar(r, "lives"), "lives address", "lives: " + Scalar(r, "lives")); g.Addrs.Add(g.Lives); g.HitAddrs.Add(g.Lives); g.SteadyAddrs.Add(g.Lives); }
            if (r.ContainsKey("lives_floor"))
            {
                int f;
                if (g.Lives < 0) throw new FormatException("rule file: 'lives_floor' without 'lives'");
                if (!int.TryParse(Scalar(r, "lives_floor"), NumberStyles.None, CultureInfo.InvariantCulture, out f) || f < 1 || f > 9) throw new FormatException("rule file: 'lives_floor' must be a whole number from 1 to 9");
                g.LivesFloor = f;
            }
            if (r.ContainsKey("input")) { g.Input = Conditions.Number(Scalar(r, "input"), "input address", "input: " + Scalar(r, "input")); g.Addrs.Add(g.Input); }
            if (r.ContainsKey("input_bits")) g.InputBits = InputBitsFrom(Scalar(r, "input_bits"));
            foreach (Dictionary<string, string> m in Maps(r, "patches", false)) g.Patches.Add(PadPatch.From(m, g));
            foreach (string range in List(r, "keep", false)) g.Keep.Add(Range("keep", range));
            foreach (string entry in List(r, "song", false))
            {
                string range = entry;
                int mask = -1;
                int amp = entry.IndexOf('&');
                if (amp >= 0)
                {
                    range = entry.Substring(0, amp).Trim();
                    mask = Conditions.Number(entry.Substring(amp + 1).Trim(), "song mask", "song: " + entry);
                }
                int[] sr = Range("song", range);
                for (int a = sr[0]; a <= sr[1]; a++) { g.Song.Add(a); g.Addrs.Add(a); if (mask >= 0) g.SongMask[a] = mask; }
            }
            if (g.Song.Count > 0 && g.Keep.Count == 0) throw new FormatException("rule file: 'song' without 'keep': nothing to carry across");
            if (r.ContainsKey("screen")) g.Screen = Conditions.Number(Scalar(r, "screen"), "screen address", "screen: " + Scalar(r, "screen"));
            foreach (string src in List(r, "barrier", false)) { g.Barrier.Add(Conditions.Compile(src, g.BarrierAddrs)); g.BarrierSrc.Add(src); }
            if (g.Barrier.Count > 0 && g.Screen < 0) throw new FormatException("rule file: 'barrier' without 'screen': barriers stop a rewind to screen");
            if (r.ContainsKey("mode")) g.Mode = Scalar(r, "mode");
            if (g.Mode != "rewind" && g.Mode != "checkpoints") throw new FormatException("rule file: 'mode' is '" + g.Mode + "': rewind or checkpoints");
            foreach (string k in g.UsesCheckpoints ? RewindOnly : CheckpointsOnly)
                if (r.ContainsKey(k)) throw new FormatException("rule file: '" + k + "' is for mode " + (g.UsesCheckpoints ? "rewind" : "checkpoints") + ", this file is mode " + g.Mode);
            if (g.UsesCheckpoints)
            {
                if (r.ContainsKey("checkpoint_wait")) g.CheckpointWait = Seconds(Scalar(r, "checkpoint_wait"), "checkpoint_wait");
                foreach (Dictionary<string, string> m in Maps(r, "checkpoints", true))
                {
                    var d = new CheckpointDef();
                    foreach (string fk in m.Keys) if (Array.IndexOf(new[] { "name", "when", "wait", "start", "levels", "patch", "no_rewind", "group" }, fk) < 0) throw new FormatException("rule file: checkpoint field '" + fk + "' unknown (name, when, wait, start, levels, patch, no_rewind, group)");
                    if (!m.ContainsKey("name") || !m.ContainsKey("when")) throw new FormatException("rule file: a checkpoint needs 'name' and 'when'");
                    d.Name = m["name"]; d.Src = m["when"];
                    d.When = Conditions.Compile(d.Src, g.CheckpointAddrs);
                    if (m.ContainsKey("patch")) d.Patches = Patch.Parse(m["patch"], "checkpoint '" + d.Name + "'");
                    d.Wait = m.ContainsKey("wait") ? Seconds(m["wait"], "checkpoint '" + d.Name + "' wait") : double.NaN;
                    if (m.ContainsKey("start"))
                    {
                        if (m["start"] != "yes" && m["start"] != "no") throw new FormatException("rule file: checkpoint '" + d.Name + "' start is '" + m["start"] + "': yes or no");
                        d.Start = m["start"] == "yes";
                    }
                    if (m.ContainsKey("levels"))
                        foreach (string lv in m["levels"].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (Array.IndexOf(Levels, lv) < 0) throw new FormatException("rule file: checkpoint '" + d.Name + "' level '" + lv + "': easy, medium or hard");
                            d.Levels.Add(lv);
                        }
                    else d.Levels.UnionWith(Levels);
                    if (m.ContainsKey("group")) d.Group = m["group"];
                    if (m.ContainsKey("no_rewind"))
                        foreach (string lv in m["no_rewind"].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (Array.IndexOf(Levels, lv) < 0) throw new FormatException("rule file: checkpoint '" + d.Name + "' no_rewind level '" + lv + "': easy, medium or hard");
                            d.NoRewind.Add(lv);
                        }
                    if (d.Start && d.Levels.Count != Levels.Length) throw new FormatException("rule file: checkpoint '" + d.Name + "' is a start: it belongs to every level");
                    g.Checkpoints.Add(d);
                }
            }
            return g;
        }

        // "a b select start up down left right": bit 0 first, each NES button once
        public static Dictionary<string, int> InputBitsFrom(string v)
        {
            string[] names = v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var bits = new Dictionary<string, int>();
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i].ToLowerInvariant();
                if (Array.IndexOf(NesButtons, n) < 0) throw new FormatException("rule file: input_bits button '" + names[i] + "': " + string.Join(", ", NesButtons));
                if (bits.ContainsKey(n)) throw new FormatException("rule file: input_bits names '" + n + "' twice");
                bits[n] = 1 << i;
            }
            if (bits.Count != 8) throw new FormatException("rule file: input_bits names the 8 NES buttons, bit 0 first (" + string.Join(" ", NesButtons) + "), not " + bits.Count);
            return bits;
        }

        static double Seconds(string v, string what)
        {
            double s;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out s) || s < 0 || s > 60) throw new FormatException("rule file: " + what + " must be seconds from 0 to 60, not '" + v + "'");
            return s;
        }

        static List<Dictionary<string, string>> Maps(Dictionary<string, object> r, string key, bool required)
        {
            var maps = new List<Dictionary<string, string>>();
            if (!r.ContainsKey(key))
            {
                if (required) throw new FormatException("rule file: '" + key + "' needs at least one item");
                return maps;
            }
            var l = r[key] as List<object>;
            if (l == null || l.Count == 0) throw new FormatException("rule file: '" + key + "' takes a list of items ('  - name: …')");
            foreach (object o in l)
            {
                var m = o as Dictionary<string, string>;
                if (m == null) throw new FormatException("rule file: '" + key + "' items are '  - field: value' maps, not '" + o + "'");
                maps.Add(m);
            }
            return maps;
        }

        static int[] Range(string key, string range)
        {
            string[] ends = range.Split('-');
            int first = Conditions.Number(ends[0].Trim(), key + " address", key + ": " + range);
            int last = ends.Length == 2 ? Conditions.Number(ends[1].Trim(), key + " address", key + ": " + range) : first;
            if (ends.Length > 2 || last < first || last - first > 0x7FF) throw new FormatException("rule file: " + key + " '" + range + "' is not '<addr>' nor '<first>-<last>' (at most 2 KB)");
            return new[] { first, last };
        }

        static string Scalar(Dictionary<string, object> r, string key)
        {
            var s = r[key] as string;
            if (s == null) throw new FormatException("rule file: '" + key + "' takes one value, not a list");
            return s;
        }

        static List<string> List(Dictionary<string, object> r, string key, bool required)
        {
            if (!r.ContainsKey(key))
            {
                if (required) throw new FormatException("rule file: '" + key + "' needs at least one condition");
                return new List<string>();
            }
            var l = new List<string>();
            var items = r[key] as List<object>;
            if (items == null) l.Add((string)r[key]);
            else
                foreach (object o in items)
                {
                    var str = o as string;
                    if (str == null) throw new FormatException("rule file: '" + key + "' takes plain items, not 'field: value' maps");
                    l.Add(str);
                }
            if (l.Count == 0) throw new FormatException("rule file: '" + key + "' needs at least one condition");
            return l;
        }

        // Any `hit` condition.
        public bool IsHit(IDictionary<int, int> cur, IDictionary<int, int> prev)
        {
            foreach (Condition c in Hit) if (c(cur, prev)) return true;
            return false;
        }
    }

    // ---------------------------------------------------------- checkpoints --
    // `mode: checkpoints` : a
    // game cut into sequences, each starting at a checkpoint; a hit always
    // goes back to the last checkpoint that counted.
    // A `patches` item: its bytes written when
    // `button` (NES buttons joined by +, read on the game's own pad byte) is
    // pressed while `when` holds, and/or from the start (`startup: yes`):
    // then written again whenever one of them reads otherwise (a game's own
    // start clears its RAM, a load brings older bytes back), so only fixed
    // values. Both in one item: kept, and also written on the press.
    public class PadPatch
    {
        public string Name;
        public int Mask;                  // the button(s), on Game.Input; 0: none
        public bool Startup;
        public Condition When;            // null: always
        public string WhenSrc;
        public List<Patch> Writes;
        public int Applied;               // times written (logged sparsely)
        // Its `when` and patch bytes: read only when it is due (the button
        // pressed, or a kept patch's check once a second), never every poll
        // (the button first, the addresses only when it is pushed: read
        // every poll they made Mega Man 1's pass 20 frames long).
        public SortedSet<int> Addrs = new SortedSet<int>();
        public List<int[]> Ranges;
        public double CheckedAt = -10;

        public static PadPatch From(Dictionary<string, string> m, Game g)
        {
            var pp = new PadPatch { Name = m.ContainsKey("name") ? m["name"] : "patch " + (g.Patches.Count + 1) };
            foreach (string fk in m.Keys) if (Array.IndexOf(new[] { "name", "button", "when", "startup", "patch" }, fk) < 0) throw new FormatException("rule file: patch field '" + fk + "' unknown (name, button, when, startup, patch)");
            if (!m.ContainsKey("patch")) throw new FormatException("rule file: patch '" + pp.Name + "' needs 'patch' (set <address> to <value>; …)");
            pp.Writes = Patch.Parse(m["patch"], "patch '" + pp.Name + "'");
            if (m.ContainsKey("startup"))
            {
                if (m["startup"] != "yes" && m["startup"] != "no") throw new FormatException("rule file: patch '" + pp.Name + "' startup is '" + m["startup"] + "': yes or no");
                pp.Startup = m["startup"] == "yes";
            }
            if (m.ContainsKey("button"))
            {
                if (g.Input < 0 || g.InputBits == null) throw new FormatException("rule file: patch '" + pp.Name + "' has a button: the file needs 'input' (the pad byte) and 'input_bits'");
                foreach (string b in m["button"].Split('+'))
                {
                    string n = b.Trim().ToLowerInvariant();
                    int bit;
                    if (!g.InputBits.TryGetValue(n, out bit)) throw new FormatException("rule file: patch '" + pp.Name + "' button '" + b.Trim() + "': " + string.Join(", ", Game.NesButtons) + ", joined by +");
                    pp.Mask |= bit;
                }
            }
            if (pp.Mask == 0 && !pp.Startup) throw new FormatException("rule file: patch '" + pp.Name + "' needs 'button' or 'startup: yes'");
            if (pp.Startup) foreach (Patch w in pp.Writes) if (w.From >= 0) throw new FormatException("rule file: patch '" + pp.Name + "' is kept from the start: fixed values only, not '" + w.Src + "'");
            if (m.ContainsKey("when"))
            {
                pp.WhenSrc = m["when"];
                if (Regex.IsMatch(pp.WhenSrc, @"\b(was|changed|unchanged|decreased|increased)\b")) throw new FormatException("rule file: patch '" + pp.Name + "' when: current values only (no was / changed / decreased / increased): it is read when the patch is due");
                pp.When = Conditions.Compile(pp.WhenSrc, pp.Addrs);
            }
            foreach (Patch w in pp.Writes) { pp.Addrs.Add(w.Addr); if (w.From >= 0) pp.Addrs.Add(w.From); }
            pp.Ranges = Watcher.Ranges(pp.Addrs);
            return pp;
        }

        // The button newly all held (on the pad byte, read every poll).
        public bool Pressed(IDictionary<int, int> cur, IDictionary<int, int> prev, int input)
        {
            return Mask != 0 && (cur[input] & Mask) == Mask && (prev[input] & Mask) != Mask;
        }

        // On its own bytes, read now: `when` holds
        public bool WhenHolds(IDictionary<int, int> vals) { return When == null || When(vals, vals); }

        // A kept patch with one of its bytes reading otherwise
        public bool Off(IDictionary<int, int> vals)
        {
            foreach (Patch w in Writes) if (vals[w.Addr] != w.Value) return true;
            return false;
        }
    }

    public class CheckpointDef
    {
        public string Name, Src;
        public Condition When;
        public double Wait = double.NaN;  // NaN: the file's checkpoint_wait
        // start: yes: a stage's first checkpoint, taken from anywhere; the
        // others only move forward from it (a later one in the file, no
        // `start` in between): an earlier sequence's screen seen again,
        // walking back, never moves the checkpoint back.
        public bool Start;
        public HashSet<string> Levels = new HashSet<string>();
        // no_rewind: the levels where, from this checkpoint until the next
        // one counts, a hit that is not a death is the game's own (the
        // damage stays); a death goes back to the last checkpoint as ever
        // (EASY's boss rooms: a death goes back to the start of the fight,
        // a hit does not). Free:
        // no_rewind holds at the level played.
        public HashSet<string> NoRewind = new HashSet<string>();
        public bool Free;
        // group: checkpoints that count in any order, and again each time
        // one turns true, while play stands on a member of the same group
        // (a checkpoint each time a Contra boss part is destroyed; the
        // parts fall in any order)
        public string Group;
        // patch: "set <addr> to <value>" or "set <addr> to <addr> + <n>"
        // ("; " between several): written after every load that lands on
        // this checkpoint (a save above a pit: Mega Man moved three widths
        // to the right).
        public List<Patch> Patches = new List<Patch>();
    }

    public class Patch
    {
        public int Addr, Value;
        public int From = -1;   // >= 0: the value is that byte (read after the load) + Value
        public string Src;

        public static List<Patch> Parse(string src, string where)
        {
            var list = new List<Patch>();
            foreach (string raw in src.Split(';'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;
                Match m = Regex.Match(part, @"^set\s+(\S+)\s+to\s+(\S+)(?:\s*([+-])\s*(\S+))?$");
                if (!m.Success) throw new FormatException("rule file: " + where + " patch '" + part + "': set <address> to <value>, or set <address> to <address> + <n>");
                var p = new Patch { Addr = Conditions.Number(m.Groups[1].Value, "patch address", part), Src = part };
                if (m.Groups[3].Success)
                {
                    p.From = Conditions.Number(m.Groups[2].Value, "patch address", part);
                    int n = Conditions.Number(m.Groups[4].Value, "patch step", part);
                    p.Value = m.Groups[3].Value == "-" ? -n : n;
                }
                else
                {
                    p.Value = Conditions.Number(m.Groups[2].Value, "patch value", part);
                    if (p.Value < 0 || p.Value > 0xFF) throw new FormatException("rule file: " + where + " patch '" + part + "': a byte, 0x00 to 0xFF");
                }
                list.Add(p);
            }
            return list;
        }

        // The byte to write, given the byte at From now (relative patches).
        public int ValueFrom(int from) { return From < 0 ? Value : (from + Value) & 0xFF; }
    }

    // Which checkpoint is seen, waits, counts or is dropped (pure,
    // unit-tested; the watcher feeds it reads and times, takes the
    // snapshots and does the rewinds).
    // A checkpoint seen is a candidate; the next snapshot taken (hit bytes
    // still) is its start; it counts once `wait` seconds of play went by
    // after that without a hit (a checkpoint just before a hit that cannot
    // be avoided must not count); a hit drops it, and a newer candidate
    // replaces an older one still waiting.
    public class Sequencer
    {
        readonly List<CheckpointDef> defs;
        readonly double defaultWait;
        bool[] was;
        // Where play stands: the last checkpoint that counted (-1: none yet).
        // A boss's health thresholds are checkpoints like the others
        // ("BossHealth decreased && BossHealth <= …", with their own
        // `levels`), in the file, not in here.
        public int Index = -1;
        public class Candidate
        {
            public string Name; public int Index = -1;
            public double Wait, SeenT, SnapT = double.NaN;
        }
        public Candidate Pending;
        public struct State { public int Index; }
        // The state at each checkpoint's snapshot (seconds of play): a
        // landing there takes it back.
        public readonly SortedDictionary<double, State> States = new SortedDictionary<double, State>();
        public readonly List<string> Events = new List<string>();   // what happened, for the log; read and cleared by the watcher

        public Sequencer(List<CheckpointDef> defs, double defaultWait)
        {
            this.defs = defs; this.defaultWait = defaultWait;
            was = new bool[defs.Count];
        }

        public int Count { get { return defs.Count; } }

        // Play stands in a free zone: the last checkpoint that counted has
        // no_rewind at this level (CheckpointDef.Free)
        public bool InFree { get { return Index >= 0 && Index < defs.Count && defs[Index].Free; } }

        // A list checkpoint may be taken now: a `start` always (but the one
        // play stands at), another only forward within the current stage.
        public bool Accepts(int i)
        {
            // a start, unless play is already in its stage, at it or past it
            // (Mega Man 6's start marker fires again on the first screen
            // later in the stage: the checkpoint would have gone back)
            if (defs[i].Start)
            {
                if (Index < i) return true;
                for (int j = i + 1; j <= Index; j++) if (defs[j].Start) return true;
                return false;
            }
            // a group's members count in any order while play stands on one
            if (defs[i].Group != null && Index >= 0 && defs[Index].Group == defs[i].Group) return true;
            if (i <= Index) return false;
            for (int j = Index + 1; j < i; j++) if (defs[j].Start) return false;
            return true;
        }

        // One read. A condition counts as it turns true (a level condition,
        // "== a screen", once on entry). Returns the new candidate, or null.
        public Candidate Look(IDictionary<int, int> cur, IDictionary<int, int> prev, double t)
        {
            Candidate found = null;
            for (int i = 0; i < defs.Count; i++)
            {
                bool holds = defs[i].When(cur, prev);
                bool rose = holds && !was[i];
                was[i] = holds;
                if (!rose) continue;
                if (!Accepts(i)) { Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_refused name={0} index={1} at={2} why=not forward", defs[i].Name, i, Index)); continue; }
                // the one already waiting, seen again (a boss threshold:
                // every later hit below it matches): its wait keeps running
                // from the first time (2026-10-11, Mega Man 2's Metal Man:
                // each hit restarted the 2 s, so it never counted)
                if (Pending != null && Pending.Index == i) continue;
                // a `start` counts at once unless it says otherwise: a hit in
                // a stage's first seconds went back to the previous stage's
                // last checkpoint; the player beams in, nothing
                // can hit him there
                double wait = !double.IsNaN(defs[i].Wait) ? defs[i].Wait : (defs[i].Start ? 0 : defaultWait);
                found = new Candidate { Name = defs[i].Name, Index = i, Wait = wait, SeenT = t };
            }
            if (found != null)
            {
                if (Pending != null) Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_replaced name={0} by={1}", Pending.Name, found.Name));
                Pending = found;
                Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_seen name={0} index={1} t={2:0.00} wait={3}", found.Name, found.Index, t, found.Wait));
            }
            return found;
        }

        // A snapshot was taken at t: the waiting candidate starts there.
        public bool Snapped(double t)
        {
            if (Pending == null || !double.IsNaN(Pending.SnapT)) return false;
            Pending.SnapT = t;
            Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_snapshot name={0} t={1:0.00}", Pending.Name, t));
            return true;
        }

        // Play time t: the candidate that counts now (its wait went by
        // without a hit), else null. Index moves to it.
        public Candidate Validate(double t)
        {
            if (Pending == null || double.IsNaN(Pending.SnapT) || t - Pending.SnapT < Pending.Wait) return null;
            Candidate c = Pending;
            Pending = null;
            Index = c.Index;
            States[c.SnapT] = new State { Index = Index };
            Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_counts name={0} t={1:0.00} index={2}", c.Name, c.SnapT, Index));
            return c;
        }

        // A hit: the candidate still waiting is dropped.
        public void Hit()
        {
            if (Pending == null) return;
            Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_dropped name={0} seen={1:0.00} why=hit", Pending.Name, Pending.SeenT));
            Pending = null;
        }

        // A landing anywhere: the state of the last checkpoint at or before
        // t, the conditions read afresh (a condition already true there does
        // not count again).
        public void LandedAt(double t, IDictionary<int, int> values)
        {
            Pending = null;
            State st = new State { Index = -1 };
            bool any = false;
            foreach (KeyValuePair<double, State> kv in States) if (kv.Key <= t + 1e-6) { st = kv.Value; any = true; }
            var later = new List<double>();
            foreach (double k in States.Keys) if (k > t + 1e-6) later.Add(k);
            foreach (double k in later) States.Remove(k);
            if (any) Index = st.Index;
            if (values != null) for (int i = 0; i < defs.Count; i++) was[i] = defs[i].When(values, values);
            Events.Add(string.Format(CultureInfo.InvariantCulture, "checkpoint_landed t={0:0.00} index={1}", t, Index));
        }

        // The checkpoint that counted with its snapshot at t (a landing
        // there gets its patches), or null.
        public CheckpointDef CountedAt(double t)
        {
            State st;
            return States.TryGetValue(t, out st) && st.Index >= 0 ? defs[st.Index] : null;
        }

        public void Reset() { Index = -1; Pending = null; States.Clear(); was = new bool[defs.Count]; }
    }

    // ------------------------------------------------------------ RetroArch --
    // RetroArch's network commands over UDP on 127.0.0.1. Some commands answer
    // (READ_CORE_MEMORY, WRITE_CORE_MEMORY, GET_STATUS, LOAD_STATE_SLOT), some
    // do not (SAVE_STATE, LOAD_STATE, SHOW_MSG): an answer is matched on its
    // first word, and what is left over is drained before the next question.
    public class RetroArch : IDisposable
    {
        readonly UdpClient udp = new UdpClient();
        // READ/WRITE_CORE_MEMORY reach memory through the core's memory map.
        // No SNES core RetroBat ships has one ("-1 no memory map defined",
        // 2026-10-11); for them READ/WRITE_CORE_RAM, the core's system
        // RAM by offset (SNES WRAM: $7E0BCF is 0x0BCF). WRITE_CORE_RAM sends
        // no answer: in that mode a write is sent, not waited on.
        public bool CoreRam;
        string Mem { get { return CoreRam ? "CORE_RAM" : "CORE_MEMORY"; } }

        // Picks the commands on one read of addr, before the watch: the RAM
        // ones only when RetroArch says the core has no memory map. The
        // answer is returned for the log.
        public string ChooseMemory(int addr)
        {
            CoreRam = false;
            string r = Ask(string.Format("READ_CORE_MEMORY {0:X4} 1", addr), 300);
            if (r != null && r.Contains("no memory map")) CoreRam = true;
            return r;
        }

        public RetroArch(int port)
        {
            udp.Connect("127.0.0.1", port);
        }

        public void Send(string cmd)
        {
            byte[] b = Encoding.ASCII.GetBytes(cmd);
            udp.Send(b, b.Length);
        }

        string Receive(int timeoutMs)
        {
            udp.Client.ReceiveTimeout = timeoutMs;
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                return Encoding.ASCII.GetString(udp.Receive(ref ep)).Trim();
            }
            catch (SocketException) { return null; }
        }

        public string Ask(string cmd, int timeoutMs)
        {
            while (udp.Available > 0) Receive(1);
            Send(cmd);
            string word = cmd.Split(' ')[0];
            for (int i = 0; i < 4; i++)
            {
                string r = Receive(timeoutMs);
                if (r == null) return null;
                if (r.StartsWith(word)) return r;
            }
            return null;
        }

        // "READ_CORE_MEMORY a8 03" -> 3; -1 when RetroArch could not read it.
        public int Read(int addr)
        {
            string r = Ask(string.Format("READ_{0} {1:X4} 1", Mem, addr), 200);
            if (r == null) return -1;
            string[] p = r.Split(' ');
            int v;
            if (p.Length >= 3 && int.TryParse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        public void Write(int addr, int value)
        {
            string cmd = string.Format("WRITE_{0} {1:X4} {2:X2}", Mem, addr, value);
            if (CoreRam) Send(cmd); else Ask(cmd, 200);
        }

        // "READ_CORE_MEMORY 500 0A 1F ..." -> the bytes; null when unreadable.
        public byte[] ReadRange(int addr, int length)
        {
            string r = Ask(string.Format("READ_{0} {1:X4} {2}", Mem, addr, length), 300);
            if (r == null) return null;
            string[] p = r.Split(' ');
            if (p.Length != length + 2) return null;
            var b = new byte[length];
            for (int i = 0; i < length; i++) if (!byte.TryParse(p[i + 2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return null;
            return b;
        }

        public void WriteRange(int addr, byte[] bytes)
        {
            var sb = new StringBuilder(string.Format("WRITE_{0} {1:X4}", Mem, addr));
            foreach (byte x in bytes) sb.AppendFormat(" {0:X2}", x);
            if (CoreRam) Send(sb.ToString()); else Ask(sb.ToString(), 300);
        }

        // "GET_STATUS PLAYING nes,Mega Man 2 (USA),crc32=..." -> "PLAYING"
        public string Status()
        {
            string r = Ask("GET_STATUS", 300);
            if (r == null) return null;
            string[] p = r.Split(' ');
            return p.Length >= 2 ? p[1] : null;
        }

        public void Dispose() { udp.Close(); }
    }

    // ------------------------------------------------------------ snapshots --
    // RetroArch cannot save into a chosen slot, and its "+1 slot" steps are
    // unreliable (see below): a snapshot is a SAVE_STATE into
    // the current slot, then a copy of the file RetroArch wrote; going back is
    // that copy put back over the slot's file, then LOAD_STATE. The game's own
    // save states in that folder are backed up first and put back at the end.
    public class Snapshots
    {
        // T: seconds of play; Values: the watched bytes read as it was taken,
        // the baseline after a rewind lands on it.
        // Unsure: bytes that moved while it was taken, left out of the
        // landing check (mode checkpoints keeps such a snapshot)
        public class Snap { public double T; public string File; public Dictionary<int, int> Values; public HashSet<int> Unsure = new HashSet<int>(); }

        readonly string statesDir, pattern, ringDir, backupDir, leftoverDir;
        readonly DateTime startedAt = DateTime.Now;
        readonly Dictionary<string, DateTime> before = new Dictionary<string, DateTime>();
        public readonly List<Snap> Ring = new List<Snap>();
        public string SlotFile;
        // The starting points' snapshots (seconds of play): never evicted by
        // Take, so L1 can go back to one however old.
        public readonly HashSet<double> Pinned = new HashSet<double>();
        int seq;
        readonly int max;

        public Snapshots(string statesDir, string romBase, string workDir, int max)
        {
            this.statesDir = statesDir;
            this.pattern = romBase + ".state";
            this.max = max;
            ringDir = Path.Combine(workDir, "ring");
            backupDir = Path.Combine(workDir, "backup");
            leftoverDir = Path.Combine(workDir, "leftover");
            foreach (string d in new[] { ringDir, backupDir })
            {
                if (Directory.Exists(d)) foreach (string f in Directory.GetFiles(d)) File.Delete(f);
                Directory.CreateDirectory(d);
            }
            int n = 0;
            foreach (string f in Mine())
            {
                before[Path.GetFileName(f)] = File.GetLastWriteTime(f);
                File.Copy(f, Path.Combine(backupDir, Path.GetFileName(f)), true);
                n++;
            }
            Log.Write("snapshots_ready", "states_dir", statesDir, "backed_up", n);
        }

        // The game's save state files and their thumbnails: "<rom>.state",
        // "<rom>.stateN", "<rom>.state(N).png".
        IEnumerable<string> Mine()
        {
            if (!Directory.Exists(statesDir)) yield break;
            foreach (string f in Directory.GetFiles(statesDir))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(n.Substring(pattern.Length), @"^\d*(\.png)?$", RegexOptions.IgnoreCase)) yield return f;
            }
        }

        // Why a save state file cannot be loaded as a whole, or null when it
        // can. RetroArch's compressed states ("#RZIPv", the default) carry
        // their own length: a 20-byte header (magic, chunk size, total size)
        // then chunks of a 4-byte length and the data; a file caught while
        // RetroArch was still writing it ends before its last chunk. An
        // uncompressed state has no such structure: only "not empty".
        public static string Incomplete(byte[] b)
        {
            if (b == null || b.Length == 0) return "empty";
            if (b.Length < 6 || Encoding.ASCII.GetString(b, 0, 6) != "#RZIPv") return null;
            if (b.Length < 20) return "header cut at " + b.Length + " bytes";
            long chunk = BitConverter.ToUInt32(b, 8);
            long total = (long)BitConverter.ToUInt64(b, 12);
            if (chunk <= 0 || total <= 0) return "header holds chunk " + chunk + ", total " + total;
            long expected = (total + chunk - 1) / chunk, n = 0, pos = 20;
            while (pos + 4 <= b.Length)
            {
                pos += 4 + (long)BitConverter.ToUInt32(b, (int)pos);
                n++;
            }
            if (pos != b.Length) return string.Format("chunk {0} runs to byte {1} of {2}", n, pos, b.Length);
            if (n != expected) return string.Format("{0} chunks of {1}", n, expected);
            return null;
        }

        // SAVE_STATE, wait for the written file, copy it into the ring. The
        // snapshot, or null when none could be taken whole.
        public Snap Take(RetroArch ra, double t, Dictionary<int, int> values)
        {
            DateTime asked = DateTime.Now.AddMilliseconds(-50);
            ra.Send("SAVE_STATE");
            string written = null;
            for (int i = 0; i < 40 && written == null; i++)
            {
                Thread.Sleep(10);
                if (SlotFile != null)
                {
                    if (File.Exists(SlotFile) && File.GetLastWriteTime(SlotFile) >= asked) written = SlotFile;
                }
                else
                {
                    foreach (string f in Mine())
                        if (!f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTime(f) >= asked) { written = f; break; }
                }
            }
            if (written == null) { Log.Write("snapshot_missing", "t", t, "slot_file", SlotFile ?? ""); return null; }
            if (SlotFile == null) { SlotFile = written; Log.Write("slot_found", "file", written); }
            // RetroArch creates the file empty, then fills it: a copy taken at
            // the first date change was sometimes 0 bytes, an empty snapshot
            // whose load did nothing and set off a death loop (Mega Man 4,
            // 2026-10-09: 139 of 140 loads). Copy once the size is non-zero
            // and has not moved over 30 ms.
            long size = -1;
            bool settled = false;
            for (int i = 0; i < 50 && !settled; i++)
            {
                Thread.Sleep(15);
                long now = new FileInfo(written).Length;
                settled = now > 0 && now == size;
                size = now;
            }
            if (!settled) { Log.Write("snapshot_unsettled", "t", t, "size", size); return null; }
            string dest = Path.Combine(ringDir, (seq++).ToString("D6") + ".state");
            for (int i = 0; ; i++)
            {
                try { File.Copy(written, dest, true); break; }
                catch (IOException) { if (i >= 10) throw; Thread.Sleep(10); }
            }
            if (new FileInfo(dest).Length != size) { Log.Write("snapshot_torn", "t", t, "size", size, "copied", new FileInfo(dest).Length); File.Delete(dest); return null; }
            string why = Incomplete(File.ReadAllBytes(dest));
            if (why != null) { Log.Write("snapshot_incomplete", "t", t, "why", why); File.Delete(dest); return null; }
            var snap = new Snap { T = t, File = dest, Values = values == null ? null : new Dictionary<int, int>(values) };
            Ring.Add(snap);
            if (Ring.Count > max)
            {
                int old = Ring.FindIndex(r => !Pinned.Contains(r.T));
                if (old >= 0) { File.Delete(Ring[old].File); Ring.RemoveAt(old); }
            }
            return snap;
        }

        // The snapshot to go back to: the latest taken at or before `target`
        // (seconds of play), else the oldest; the ones after it are dropped.
        public Snap Pick(double target)
        {
            // an empty file (RetroArch's file caught before it was filled)
            // would load nothing: never chosen, gone from the ring
            for (int i = Ring.Count - 1; i >= 0; i--)
                if (!File.Exists(Ring[i].File) || new FileInfo(Ring[i].File).Length == 0)
                {
                    Log.Write("snapshot_empty", "t", Ring[i].T);
                    if (File.Exists(Ring[i].File)) File.Delete(Ring[i].File);
                    Ring.RemoveAt(i);
                }
            if (Ring.Count == 0) return null;
            int found = 0;
            for (int i = Ring.Count - 1; i >= 0; i--) if (Ring[i].T <= target + 1e-6) { found = i; break; }
            int keepAt = found;
            for (int i = Ring.Count - 1; i > keepAt; i--) { File.Delete(Ring[i].File); Ring.RemoveAt(i); }
            return Ring[keepAt];
        }

        // A new starting point (a stage's first snapshot): what came before it
        // belongs to another stage, and no rewind may land there.
        public void DropBefore(double t)
        {
            while (Ring.Count > 0 && Ring[0].T < t - 1e-6) { File.Delete(Ring[0].File); Ring.RemoveAt(0); }
        }

        // A snapshot whose load did not land: out of the ring.
        public void Discard(Snap s)
        {
            Ring.Remove(s);
            if (File.Exists(s.File)) File.Delete(s.File);
        }
        // Put the snapshot in the slot and load it. False (nothing sent) when
        // the slot does not hold the snapshot's exact bytes after the copy: a
        // save still in flight (a SAVE_STATE whose file came too late to be
        // taken) can land on the slot after the copy, and LOAD_STATE would
        // load that instead.
        public bool Load(RetroArch ra, Snap s)
        {
            byte[] want = File.ReadAllBytes(s.File);
            string why = Incomplete(want);
            if (why != null) { Log.Write("load_refused", "snapshot", s.T, "why", why); return false; }
            for (int i = 0; ; i++)
            {
                try { File.Copy(s.File, SlotFile, true); break; }
                catch (IOException) { if (i >= 10) { Log.Write("load_copy_failed", "snapshot", s.T); return false; } Thread.Sleep(10); }
            }
            Thread.Sleep(5);
            byte[] got;
            try { got = File.ReadAllBytes(SlotFile); } catch (IOException) { got = null; }
            if (got == null || got.Length != want.Length || !Same(got, want)) { Log.Write("slot_overwritten", "snapshot", s.T, "slot_bytes", got == null ? -1 : got.Length, "snapshot_bytes", want.Length); return false; }
            ra.Send("LOAD_STATE");
            return true;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Put the game's own save states back as they were: a changed one from
        // the backup, a new one (written during this session) moved aside to
        // leftover\, never deleted. Refuses past 4 new files: that is not us.
        public void Restore()
        {
            int restored = 0, moved = 0;
            var created = new List<string>();
            foreach (string f in Mine())
            {
                string n = Path.GetFileName(f);
                if (before.ContainsKey(n))
                {
                    if (File.GetLastWriteTime(f) != before[n]) { File.Copy(Path.Combine(backupDir, n), f, true); File.SetLastWriteTime(f, before[n]); restored++; }
                }
                else if (File.GetLastWriteTime(f) >= startedAt) created.Add(f);
            }
            if (created.Count > 4) { Log.Write("restore_refused", "new_files", created.Count, "first", created[0]); return; }
            Directory.CreateDirectory(leftoverDir);
            foreach (string f in created) { File.Copy(f, Path.Combine(leftoverDir, Path.GetFileName(f)), true); File.Delete(f); moved++; }
            Log.Write("restored", "restored", restored, "moved_aside", moved);
        }
    }

    // -------------------------------------------------------------- options --
    // The player's options: RetroBat's standard ES options, declared in
    // es_features_celeste.cfg for the `celeste` emulator, stored by ES in
    // es_settings.cfg as celeste.<key> (the system's advanced settings) and
    // celeste["<rule>.yaml"].<key> (a game's own options, which win). Read
    // here, never through ES or emulatorLauncher: the system stays portable.
    // Each option settles on a value and says where it came from (default,
    // system, game), or why the game ignores it (no such byte in the rule).
    public class Options
    {
        public class Setting { public string Key, Value, Source, Ignored; }
        public const string RewindTime = "rewind_time", ToScreen = "rewind_to_screen", Music = "continuous_music", CountLives = "count_lives", CheckpointLevel = "checkpoints";
        public readonly List<Setting> Settings = new List<Setting>();
        public double Rewind, Hold = 10;
        public bool Screen, KeepMusic, WriteLives;
        public int ScreenN;   // REWIND TO SCREEN: screens a hit goes back, 0 = off
        public string Level = "medium";   // CHECKPOINTS: the difficulty's checkpoints (mode checkpoints)

        // es_settings.cfg's <string name="…" value="…" /> entries, as ES
        // writes them (one per line, name first), entities decoded: a game's
        // key holds &quot; (celeste[&quot;Contra (USA).yaml&quot;].count_lives).
        // Read as text: System.Xml is not referenced where this file compiles.
        public static Dictionary<string, string> ReadSettings(string path)
        {
            return ParseSettings(File.ReadAllText(path, Encoding.UTF8));
        }

        public static Dictionary<string, string> ParseSettings(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(text, "<string\\s+name=\"([^\"]*)\"\\s+value=\"([^\"]*)\""))
                d[System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)] = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
            return d;
        }

        // The game's value, else the system's, else none ("" and "auto" are
        // AUTO: not set at that level).
        static Setting Pick(IDictionary<string, string> es, string rule, string key)
        {
            string v;
            if (es.TryGetValue("celeste[\"" + rule + "\"]." + key, out v) && v != "" && v != "auto") return new Setting { Key = key, Value = v, Source = "game" };
            if (es.TryGetValue("celeste." + key, out v) && v != "" && v != "auto") return new Setting { Key = key, Value = v, Source = "system" };
            return new Setting { Key = key, Value = "auto", Source = "default" };
        }

        static bool On(Setting s, bool auto)
        {
            if (s.Value == "auto") return auto;
            if (s.Value == "1") return true;
            if (s.Value == "0") return false;
            throw new FormatException("es_settings.cfg: " + s.Key + " (" + s.Source + ") is '" + s.Value + "': 1, 0 or auto");
        }

        // rule: the rule file's name ("Contra (USA).yaml"), as ES keys a game.
        public static Options Resolve(IDictionary<string, string> es, string rule, Game g)
        {
            var o = new Options();
            Setting screen = Pick(es, rule, ToScreen);
            if (g.UsesCheckpoints) { screen.Ignored = "mode checkpoints"; o.Screen = false; }
            else if (g.Screen < 0) { screen.Ignored = "no screen byte in the rule"; o.Screen = false; }
            else
            {
                // OFF / 1 / 2 / 3; "1" is also what the earlier ON / OFF switch
                // stored for ON
                if (screen.Value == "auto" || screen.Value == "0") o.ScreenN = 0;
                else if (screen.Value == "1" || screen.Value == "2" || screen.Value == "3") o.ScreenN = screen.Value[0] - '0';
                else throw new FormatException("es_settings.cfg: " + ToScreen + " (" + screen.Source + ") is '" + screen.Value + "': 0, 1, 2, 3 or auto");
                o.Screen = o.ScreenN > 0;
            }

            Setting time = Pick(es, rule, RewindTime);
            double sec;
            if (time.Value == "auto") sec = g.Rewind;
            else if (time.Value == "none") sec = double.PositiveInfinity;
            else if (!double.TryParse(time.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out sec) || sec <= 0)
                throw new FormatException("es_settings.cfg: " + RewindTime + " (" + time.Source + ") is '" + time.Value + "': seconds, none or auto");
            // REWIND TO SCREEN: a hit goes to a starting point, and the
            // rewind time is the hold past the floor (Rewinder.Floor)
            if (g.UsesCheckpoints) { time.Ignored = "mode checkpoints"; o.Rewind = double.PositiveInfinity; }
            else if (o.Screen) { o.Rewind = double.PositiveInfinity; o.Hold = double.IsInfinity(sec) ? 10 : sec; }
            else o.Rewind = sec;

            Setting level = Pick(es, rule, CheckpointLevel);
            if (!g.UsesCheckpoints) level.Ignored = "mode rewind";
            else if (level.Value != "auto")
            {
                if (Array.IndexOf(Game.Levels, level.Value) < 0) throw new FormatException("es_settings.cfg: " + CheckpointLevel + " (" + level.Source + ") is '" + level.Value + "': easy, medium, hard or auto");
                o.Level = level.Value;
            }

            Setting music = Pick(es, rule, Music);
            if (g.Song.Count == 0) { music.Ignored = "no song byte in the rule"; o.KeepMusic = g.Keep.Count > 0; }
            else o.KeepMusic = On(music, true);

            Setting lives = Pick(es, rule, CountLives);
            if (g.Lives < 0) { lives.Ignored = "no lives byte in the rule"; o.WriteLives = false; }
            else o.WriteLives = On(lives, true);

            o.Settings.AddRange(new[] { time, screen, music, lives, level });
            return o;
        }

        // The settled values onto the game, before the watcher starts.
        public void Apply(Game g)
        {
            g.Rewind = Rewind;
            g.Hold = Hold;
            g.ScreenN = ScreenN;
            if (Screen) { g.Addrs.Add(g.Screen); g.Addrs.UnionWith(g.BarrierAddrs); }
            if (g.UsesCheckpoints)
            {
                g.Level = Level;
                g.Checkpoints = g.Checkpoints.FindAll(c => c.Levels.Contains(Level));
                foreach (CheckpointDef c in g.Checkpoints) c.Free = c.NoRewind.Contains(Level);
                g.Addrs.UnionWith(g.CheckpointAddrs);
            }
            if (!KeepMusic) { g.Keep.Clear(); g.Song.Clear(); }
            g.WriteLives = WriteLives;
        }

        public void LogAll()
        {
            foreach (Setting s in Settings)
                Log.Write("option", "key", s.Key, "value", s.Value, "from", s.Source, "ignored", s.Ignored ?? "");
            Log.Write("options", "rewind", double.IsInfinity(Rewind) ? "to the starting point" : Rewind.ToString(CultureInfo.InvariantCulture), "rewind_to_screen", ScreenN, "checkpoints", Level, "hold_s", Screen ? Hold : double.NaN, "continuous_music", KeepMusic, "count_lives", WriteLives);
        }
    }

    // ------------------------------------------------------------- rewinder --
    // Where a death sends the player, and the idle counter (pure, unit-tested):
    // - a death goes back to the later of the last starting point (where the
    //   last rewind landed, a `checkpoint`, or the stage start) and `rewind`
//   seconds before it:
    //   never further back than where the player last started;
    // - a death starts a counter unless one runs; any pad input stops it; after
    //   `rewind` seconds of play without input, the player goes back `rewind`
    //   seconds before the last starting point (out of an unfair spot), and the
    //   death count returns to what it was when the counter started, that
    //   first death included: only the deaths not fought are forgiven. Being
    //   moved back never starts a counter.
    public class Rewinder
    {
        public readonly double Rewind;
        public int Deaths;
        public double Checkpoint = double.NaN;  // seconds of play; NaN: none yet
        public bool Counting;
        public double Idle;
        public int DeathsAtCounter;

        // Every starting point the player went through, oldest first, with
        // the death count as they arrived there: L1 goes back to the one
        // before the last (Back), deaths as they were then.
        // Barrier: a hit in REWIND TO SCREEN never goes back past it;
        // Why: the `barrier:` condition that made it one.
        public struct StartPoint { public double T; public int Deaths; public bool Barrier; public string Why; }
        public readonly List<StartPoint> StartPoints = new List<StartPoint>();
        public const int StartPointsKept = 20;

        // The idle counter's length and the escape's step back: the rewind,
        // or 10 s when the rewind has no limit (REWIND TIME none, REWIND TO
        // SCREEN: a death goes back to the starting point itself).
        public readonly double Step;

        // REWIND TO SCREEN's floor: the starting
        // point the last hit landed on; a hit never goes back before it
        // ("longer scenes" stay won). Once more than Hold seconds were played
        // past it, a hit that would land on it goes to Hold seconds back
        // instead, which becomes the floor; n screens further on, the N back
        // count is past it anyway. NaN: none yet.
        public double Floor = double.NaN;
        public readonly double Hold;

        public Rewinder(double rewind, double hold = 10) { Rewind = rewind; Step = double.IsInfinity(rewind) ? 10 : rewind; Hold = hold; }

        // A hit at t: counts the death, starts the counter unless one runs, and
        // returns the time to go back to.
        public double OnHit(double t)
        {
            Deaths++;
            if (!Counting) { Counting = true; Idle = 0; DeathsAtCounter = Deaths; }
            return double.IsNaN(Checkpoint) ? t - Rewind : Math.Max(Checkpoint, t - Rewind);
        }

        // dt seconds of play, pad pressed or not. True: the counter ran out,
        // go back to Escape.
        public bool Tick(double dt, bool pressed)
        {
            if (!Counting) return false;
            if (pressed) { Counting = false; return false; }
            Idle += dt;
            return Idle >= Step;
        }

        public double Escape()
        {
            Counting = false;
            Deaths = DeathsAtCounter;
            return (double.IsNaN(Checkpoint) ? 0 : Checkpoint) - Step;
        }

        // Where a load landed (or the first snapshot of a stage): the
        // starting points from t on are gone (their snapshots too), t is the
        // last one, with the deaths now.
        // A starting point landed on again keeps its barrier.
        public void Landed(double t, bool barrier = false, string why = null)
        {
            Checkpoint = t;
            while (StartPoints.Count > 0 && StartPoints[StartPoints.Count - 1].T >= t - 1e-6)
            {
                StartPoint gone = StartPoints[StartPoints.Count - 1];
                if (Math.Abs(gone.T - t) < 1e-6 && gone.Barrier && !barrier) { barrier = true; why = gone.Why; }
                StartPoints.RemoveAt(StartPoints.Count - 1);
            }
            StartPoints.Add(new StartPoint { T = t, Deaths = Deaths, Barrier = barrier, Why = why });
            if (StartPoints.Count > StartPointsKept) StartPoints.RemoveAt(0);
        }

        // REWIND TO SCREEN: the n-th starting point counting back from
        // the last (1: the last), never past the last barrier nor the Floor;
        // stopped: a barrier cut it short; floored: the Floor did. Fewer
        // than n points: the oldest kept.
        public StartPoint ScreenTarget(int n, out bool stopped) { bool floored; return ScreenTarget(n, out stopped, out floored); }

        public StartPoint ScreenTarget(int n, out bool stopped, out bool floored)
        {
            stopped = false; floored = false;
            int last = StartPoints.Count - 1;
            int floor = 0, used = -1;
            for (int i = last; i >= 0; i--) if (StartPoints[i].Barrier) { floor = i; break; }
            if (!double.IsNaN(Floor))
                for (int i = last; i >= 0; i--) if (Math.Abs(StartPoints[i].T - Floor) < 1e-6) { used = i; break; }
            int at = last - (n - 1);
            if (used >= 0 && at <= used && used >= floor) { at = used; floored = true; }
            else if (at < floor) { at = floor; stopped = StartPoints[floor].Barrier; }
            if (at < 0) at = 0;
            return StartPoints[at];
        }

        // A hit with REWIND TO SCREEN at t: one death, the counter as OnHit,
        // and the time to go back to: the n-th starting point back, or, when
        // the Floor holds it and more than Hold seconds were played past the
        // Floor, Hold seconds back (why: "screen", "floor", "after_hold").
        // The death count is not the point's (only L1 restores one).
        public double OnHitScreen(double t, int n, out bool stopped, out string why)
        {
            Deaths++;
            if (!Counting) { Counting = true; Idle = 0; DeathsAtCounter = Deaths; }
            bool floored;
            StartPoint sp = ScreenTarget(n, out stopped, out floored);
            why = floored ? "floor" : "screen";
            if (floored && t - sp.T > Hold) { why = "after_hold"; return t - Hold; }
            return sp.T;
        }

        // A hit in mode checkpoints: one death, the counter as OnHit,
        // back to the last checkpoint that counted (the last starting point).
        public double OnHitCheckpoint(double t)
        {
            Deaths++;
            if (!Counting) { Counting = true; Idle = 0; DeathsAtCounter = Deaths; }
            return double.IsNaN(Checkpoint) ? t - Step : Checkpoint;
        }

        // The hit's load landed: that point is the Floor now.
        public void UsedLanding() { Floor = Checkpoint; }

        // The last barrier's time, or NaN: the idle counter's escape in REWIND
        // TO SCREEN stops there too.
        public double LastBarrier()
        {
            for (int i = StartPoints.Count - 1; i >= 0; i--) if (StartPoints[i].Barrier) return StartPoints[i].T;
            return double.NaN;
        }

        // The starting point before the last, or false when there is none.
        // Nothing changes until the load lands (Landed).
        public bool Previous(out StartPoint p)
        {
            p = default(StartPoint);
            if (StartPoints.Count < 2) return false;
            p = StartPoints[StartPoints.Count - 2];
            return true;
        }

        // A new stage: the next snapshot is its starting point.
        public void NewStage() { Checkpoint = double.NaN; Counting = false; StartPoints.Clear(); Floor = double.NaN; }
    }

    // ----------------------------------------------------------- pad switch --
    // ○ held 1 s turns Celeste mode off, △ held 1 s back on (a tap does
    // nothing: see Hold): a way out of a bug that still slips through.
    // The NES game never sees these:
    // on this setup ✕ is jump and □ fire, ○ and △ go to RetroPad X and Y,
    // which fceumm leaves unused. So the watcher reads the DualSense itself,
    // over HID, as PadAudio does (its discovery is reused: PadAudio.cs is
    // compiled with this file). Every open handle gets every input report, so
    // reading takes nothing from RetroArch. Any pad switches.
    // A button that acts only when held HoldMs (pure, unit-tested): a tap
    // during play does nothing. ○ pressed as a game button turned Celeste
    // mode off unseen in Bad Dudes and Adventure Island III (the games
    // must never see the save states).
    public class Hold
    {
        public const long HoldMs = 1000;
        long since = -1;
        bool fired;
        // The button's state at ms. Fired: held long enough, once per press.
        // Tapped: released before that (logged, ignored).
        public enum Event { None, Fired, Tapped }
        public Event Update(bool down, long ms)
        {
            if (down)
            {
                if (since < 0) { since = ms; fired = false; return Event.None; }
                if (!fired && ms - since >= HoldMs) { fired = true; return Event.Fired; }
                return Event.None;
            }
            if (since < 0) return Event.None;
            bool tap = !fired;
            since = -1;
            return tap ? Event.Tapped : Event.None;
        }
    }

    // The three buttons, by the game's system: ones its core never sees.
    // NES: ○ off, △ on, L1 back (fceumm leaves RetroPad X, Y and L unused).
    // SNES: every face button and L1/R1 are the game's (Mega Man X
    // dashes on ○ = A, switches weapons on L/R), but L2, L3 and R3 go to
    // RetroPad L2, L3 and R3, which no SNES core reads: L3 held off, R3 held
    // on, L2 back. Bits of PadSwitch.Buttons (face | next byte << 8).
    public class Keys
    {
        public int Off, On, Back;
        public string OffName, OnName, BackName;
        public const int Circle = 0x40, Triangle = 0x80, L1 = 0x100, L2 = 0x400, L3 = 0x4000, R3 = 0x8000;

        // A system with no known free buttons is refused: the launch fails
        // with its name, never runs with buttons the game also uses.
        public static Keys For(string system)
        {
            switch ((system ?? "").ToLowerInvariant())
            {
                case "nes": return new Keys { Off = Circle, OffName = "circle", On = Triangle, OnName = "triangle", Back = L1, BackName = "L1" };
                case "snes": return new Keys { Off = L3, OffName = "L3", On = R3, OnName = "R3", Back = L2, BackName = "L2" };
                default: throw new ArgumentException("Celeste mode knows no free pad buttons for the system '" + system + "' (its off, on and back buttons): add them to Celeste.Keys");
            }
        }
    }

    public class PadSwitch : IDisposable
    {
        readonly Keys keys;
        readonly Action<bool> turn;   // false: off, true: on
        readonly Action pressed;      // any button or the D-pad, pressed
        readonly Action back;         // back to the starting point before the last
        public volatile int Reports;  // input reports read, all pads
        readonly Dictionary<string, Thread> readers = new Dictionary<string, Thread>();
        volatile bool stop;
        Thread scanner;

        public PadSwitch(Keys keys, Action<bool> turn, Action pressed, Action back) { this.keys = keys; this.turn = turn; this.pressed = pressed; this.back = back; }

        // The face byte and the next one (L1 R1 L2 R2 Create Options L3 R3)
        // as face | next << 8, or -1.
        public static int Buttons(bool bluetooth, byte[] b, int n)
        {
            int face = Face(bluetooth, b, n);
            if (face < 0) return -1;
            int at = b[0] == 0x31 ? 10 : (bluetooth ? 6 : 9);
            return face | (n > at ? b[at] << 8 : 0);
        }

        // Anything held: a face button, the D-pad off its neutral 8, or one
        // of the next byte's buttons (Options starts most games).
        public static bool AnyHeld(int buttons)
        {
            return buttons >= 0 && ((buttons & 0xF0) != 0 || (buttons & 0x0F) != 8 || (buttons >> 8) != 0);
        }

        // The face-buttons byte of an input report (D-pad in the low nibble,
        // 8 = released; □ 0x10, ✕ 0x20, ○ 0x40, △ 0x80), or -1: Bluetooth
        // extended 0x31 byte 9, Bluetooth simple 0x01 byte 5, USB 0x01 byte 8.
        public static int Face(bool bluetooth, byte[] b, int n)
        {
            if (n >= 10 && b[0] == 0x31) return b[9];
            if (b[0] == 0x01) { if (bluetooth && n >= 6) return b[5]; if (!bluetooth && n >= 9) return b[8]; }
            return -1;
        }

        public void Start()
        {
            scanner = new Thread(Scan) { IsBackground = true };
            scanner.Start();
        }

        void Scan()
        {
            while (!stop)
            {
                try
                {
                    foreach (PadAudio.PadInfo p in PadAudio.Pad.Find(false))
                    {
                        lock (readers)
                        {
                            Thread t;
                            if (readers.TryGetValue(p.Path, out t) && t.IsAlive) continue;
                            PadAudio.PadInfo pad = p;
                            t = new Thread(() => Read(pad)) { IsBackground = true };
                            readers[p.Path] = t;
                            t.Start();
                        }
                    }
                }
                catch (Exception e) { Log.Write("pad_scan_failed", "error", e.Message); }
                for (int i = 0; i < 50 && !stop; i++) Thread.Sleep(100);
            }
        }

        void Read(PadAudio.PadInfo p)
        {
            string serial = p.Serial;
            try
            {
                using (var h = PadAudio.Native.CreateFile(p.Path, PadAudio.Native.GENERIC_READ, PadAudio.Native.SHARE_RW, IntPtr.Zero, PadAudio.Native.OPEN_EXISTING, PadAudio.Native.OVERLAPPED, IntPtr.Zero))
                {
                    if (h.IsInvalid) { Log.Write("pad_open_failed", "pad", serial, "win32", System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return; }
                    using (var input = new FileStream(h, FileAccess.Read, p.InputLength, true))
                    {
                        Log.Write("pad_reading", "pad", serial, "bluetooth", p.Bluetooth, "report_bytes", p.InputLength);
                        byte[] buf = new byte[p.InputLength];
                        int reports = 0;
                        var clock = Stopwatch.StartNew();
                        var state = new PadState();
                        while (!stop)
                        {
                            IAsyncResult ar = input.BeginRead(buf, 0, buf.Length, null, null);
                            while (!ar.AsyncWaitHandle.WaitOne(500)) if (stop) return;
                            int n = input.EndRead(ar);
                            int face = Buttons(p.Bluetooth, buf, n);
                            if (face < 0) continue;
                            if (reports++ == 0) Log.Write("pad_first_report", "pad", serial, "report", buf[0], "face", face & 0xFF);
                            Reports++;
                            Handle(state, face, clock.ElapsedMilliseconds, serial);
                        }
                    }
                }
            }
            catch (Exception e) { Log.Write("pad_lost", "pad", serial, "error", e.Message); }
        }

        // One pad's buttons (the DualSense bit layout, see Buttons; other pads
        // are put in it by SdlPads.Layout) read at ms: the off and on holds,
        // the back press and any first press. Every pad goes through here.
        public class PadState { public int Last = -1; public readonly Hold Off = new Hold(), On = new Hold(); }

        public void Handle(PadState st, int buttons, long ms, string pad)
        {
            if (st.Last >= 0)
            {
                Hold.Event ce = st.Off.Update((buttons & keys.Off) != 0, ms);
                if (ce == Hold.Event.Fired) turn(false);
                else if (ce == Hold.Event.Tapped) Log.Write("switch_tap_ignored", "button", keys.OffName, "pad", pad);
                Hold.Event te = st.On.Update((buttons & keys.On) != 0, ms);
                if (te == Hold.Event.Fired) turn(true);
                else if (te == Hold.Event.Tapped) Log.Write("switch_tap_ignored", "button", keys.OnName, "pad", pad);
                if ((buttons & keys.Back) != 0 && (st.Last & keys.Back) == 0) back();
                if (AnyHeld(buttons) && !AnyHeld(st.Last)) pressed();
            }
            st.Last = buttons;
        }

        // Every other pad (Xbox, 8BitDo, a generic DirectInput pad), through
        // RetroArch's own SDL2.dll; null: DualSense only.
        public SdlPads Sdl;

        public void Dispose() { stop = true; if (Sdl != null) Sdl.Dispose(); }
    }

    // The pads that are no DualSense (any pad with 4 face buttons and L/R),
    // read through the SDL2.dll RetroArch ships and reads them
    // with (input_joypad_driver sdl2), on a thread of their own, beside
    // RetroArch: XInput and DirectInput pads are shared. By button position,
    // put in the DualSense layout, so the system's buttons are the same
    // places (NES: the right face button held off, the top one held on, L
    // back). SDL's own DualSense and DualShock code (HIDAPI) is off: it
    // writes settings to the pad, and the DualSense has its own reader here
    // (with PadAudio); a Sony DualSense SDL still lists is skipped. Nothing
    // here can fail a launch: no DLL, no pad, a pad lost, all logged.
    public class SdlPads : IDisposable
    {
        const string D = "SDL2.dll";
        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        // the DLL's own folder searched for what it needs (RetroArch's, not
        // PowerShell's: a bare LoadLibrary failed with 126, 2026-10-11)
        const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern int SDL_SetHint(string name, string value);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern int SDL_Init(uint flags);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern void SDL_Quit();
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern IntPtr SDL_GetError();
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern void SDL_PumpEvents();
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern int SDL_NumJoysticks();
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern int SDL_IsGameController(int index);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern IntPtr SDL_JoystickNameForIndex(int index);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern ushort SDL_JoystickGetDeviceVendor(int index);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern ushort SDL_JoystickGetDeviceProduct(int index);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern IntPtr SDL_GameControllerOpen(int index);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern void SDL_GameControllerClose(IntPtr pad);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern int SDL_GameControllerGetAttached(IntPtr pad);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern void SDL_GameControllerUpdate();
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern byte SDL_GameControllerGetButton(IntPtr pad, int button);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern short SDL_GameControllerGetAxis(IntPtr pad, int axis);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern ushort SDL_GameControllerGetVendor(IntPtr pad);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern ushort SDL_GameControllerGetProduct(IntPtr pad);
        [System.Runtime.InteropServices.DllImport(D, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] static extern IntPtr SDL_GameControllerName(IntPtr pad);
        const uint INIT_JOYSTICK = 0x200, INIT_GAMECONTROLLER = 0x2000, INIT_EVENTS = 0x4000;
        // SDL_GameControllerButton (2.0.14) and the trigger axes
        public const int A = 0, B = 1, X = 2, Y = 3, BackButton = 4, StartButton = 6, LeftStick = 7, RightStick = 8, LeftShoulder = 9, RightShoulder = 10,
            DpadUp = 11, DpadDown = 12, DpadLeft = 13, DpadRight = 14, TriggerLeft = 4, TriggerRight = 5;
        public const ushort Sony = 0x054C;
        public static readonly ushort[] DualSense = { 0x0CE6, 0x0DF2 };

        readonly PadSwitch sw;
        readonly string dll;
        volatile bool stop;
        Thread th;
        public volatile int Opened;

        public SdlPads(PadSwitch sw, string dll) { this.sw = sw; this.dll = dll; }

        public void Start() { th = new Thread(Run) { IsBackground = true }; th.Start(); }

        static string Str(IntPtr p) { return p == IntPtr.Zero ? "" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p); }

        // A pad's buttons by position (SDL's A is the bottom face button
        // whatever its label, with button labels off) in the DualSense
        // layout: □ west 0x10, ✕ south 0x20, ○ east 0x40, △ north 0x80, the
        // D-pad nibble (8 = released), L1 R1 L2 R2 Create Options L3 R3 in
        // the next byte. Pure: `held` answers a button, `axis` a trigger.
        public static int Layout(Func<int, bool> held, Func<int, int> axis)
        {
            int b = 0;
            bool up = held(DpadUp), down = held(DpadDown), left = held(DpadLeft), right = held(DpadRight);
            int dpad = 8;
            if (up && right) dpad = 1; else if (down && right) dpad = 3; else if (down && left) dpad = 5; else if (up && left) dpad = 7;
            else if (up) dpad = 0; else if (right) dpad = 2; else if (down) dpad = 4; else if (left) dpad = 6;
            b |= dpad;
            if (held(X)) b |= 0x10;
            if (held(A)) b |= 0x20;
            if (held(B)) b |= 0x40;
            if (held(Y)) b |= 0x80;
            if (held(LeftShoulder)) b |= 0x100;
            if (held(RightShoulder)) b |= 0x200;
            if (axis(TriggerLeft) > 16000) b |= 0x400;
            if (axis(TriggerRight) > 16000) b |= 0x800;
            if (held(BackButton)) b |= 0x1000;
            if (held(StartButton)) b |= 0x2000;
            if (held(LeftStick)) b |= 0x4000;
            if (held(RightStick)) b |= 0x8000;
            return b;
        }

        public static bool Skipped(ushort vendor, ushort product) { return vendor == Sony && Array.IndexOf(DualSense, product) >= 0; }

        void Run()
        {
            try
            {
                if (LoadLibraryEx(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero) { Log.Write("sdl_unavailable", "dll", dll, "win32", System.Runtime.InteropServices.Marshal.GetLastWin32Error()); return; }
                // the pad read with no window of ours in front
                SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
                // Sony pads through DirectInput like any pad, never written to
                SDL_SetHint("SDL_JOYSTICK_HIDAPI", "0");
                // raw input can take a pad for itself: XInput / DirectInput only
                SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
                // by position: a Nintendo-style pad's A on the right reads as B
                SDL_SetHint("SDL_GAMECONTROLLER_USE_BUTTON_LABELS", "0");
                if (SDL_Init(INIT_JOYSTICK | INIT_GAMECONTROLLER | INIT_EVENTS) != 0) { Log.Write("sdl_unavailable", "dll", dll, "error", Str(SDL_GetError())); return; }
                Log.Write("sdl_ready", "dll", dll);
                var pads = new List<IntPtr>();
                var states = new Dictionary<IntPtr, PadSwitch.PadState>();
                var names = new Dictionary<IntPtr, string>();
                var clock = Stopwatch.StartNew();
                int count = -1;
                long nextScan = 0;
                while (!stop)
                {
                    SDL_PumpEvents();
                    long ms = clock.ElapsedMilliseconds;
                    if (ms >= nextScan)
                    {
                        nextScan = ms + 2000;
                        int n = SDL_NumJoysticks();
                        bool lost = pads.Exists(p => SDL_GameControllerGetAttached(p) == 0);
                        if (n != count || lost)
                        {
                            foreach (IntPtr p in pads) SDL_GameControllerClose(p);
                            pads.Clear(); states.Clear(); names.Clear();
                            for (int i = 0; i < n; i++)
                            {
                                if (SDL_IsGameController(i) == 0) { Log.Write("sdl_pad_skipped", "index", i, "name", Str(SDL_JoystickNameForIndex(i)), "vendor", SDL_JoystickGetDeviceVendor(i).ToString("X4"), "product", SDL_JoystickGetDeviceProduct(i).ToString("X4"), "why", "no controller mapping"); continue; }
                                IntPtr p = SDL_GameControllerOpen(i);
                                if (p == IntPtr.Zero) { Log.Write("sdl_pad_failed", "index", i, "error", Str(SDL_GetError())); continue; }
                                ushort v = SDL_GameControllerGetVendor(p), pr = SDL_GameControllerGetProduct(p);
                                string name = Str(SDL_GameControllerName(p));
                                if (Skipped(v, pr)) { Log.Write("sdl_pad_skipped", "index", i, "name", name, "why", "DualSense: read over HID"); SDL_GameControllerClose(p); continue; }
                                pads.Add(p); states[p] = new PadSwitch.PadState(); names[p] = name + " " + v.ToString("X4") + ":" + pr.ToString("X4");
                                Log.Write("sdl_pad", "index", i, "name", name, "vendor", v.ToString("X4"), "product", pr.ToString("X4"));
                            }
                            count = n;
                            Opened = pads.Count;
                            Log.Write("sdl_pads", "joysticks", n, "read", pads.Count);
                        }
                    }
                    SDL_GameControllerUpdate();
                    foreach (IntPtr p in pads)
                    {
                        IntPtr pad = p;
                        int b = Layout(btn => SDL_GameControllerGetButton(pad, btn) != 0, ax => SDL_GameControllerGetAxis(pad, ax));
                        if (states[pad].Last != b) sw.Reports++;
                        sw.Handle(states[pad], b, ms, names[pad]);
                    }
                    Thread.Sleep(10);
                }
                foreach (IntPtr p in pads) SDL_GameControllerClose(p);
                SDL_Quit();
            }
            catch (Exception e) { Log.Write("sdl_failed", "error", e.Message); }
        }

        public void Dispose() { stop = true; }
    }

    // -------------------------------------------------------------- watcher --
    public class Watcher
    {
        readonly Game game;
        readonly RetroArch ra;
        readonly Snapshots snaps;
        public readonly Rewinder Rew;
        // Seconds the game ran (RetroArch PLAYING), moved back by every
        // rewind: `rewind: 10` is 10 real seconds of the game running.
        public double T;
        public volatile bool Stop;
        double lastLoadAt = -10;
        // The first starting point (and the one after △) is taken only once
        // the level hit bytes (health, lives) have been still for Steady
        // seconds and no death marker shows its value (Game.SteadyAddrs,
        // Game.Markers; a marker may move freely):
        // Mega Man 5's came while a hit was under way, so every death landed
        // a frame before that same hit (2026-10-09).
        public const double Steady = 1.0;
        double hitMovedAt = -100;
        bool screenPending;
        // REWIND TO SCREEN: the game time of the last screen change,
        // for the start point's delay; the clock time of the last snapshot
        // taken, for the floor between two early ones
        double screenChangedT = double.NaN, lastSnapAt = -100;
        // an early snapshot on a screen change, at most 4 a second: walking
        // back and forth over a screen edge
        public const double EarlyFloor = 0.25;
        public enum Early { Now, Deferred, HitMoving }
        // Snapshot at once on a screen change: not while the hit bytes moved
        // within Steady (a start point taken mid-hit lands every rewind on
        // that hit, Mega Man 5), not within EarlyFloor of the last snapshot;
        // both keep the once-a-second rhythm.
        public static Early EarlySnapshot(double now, double lastSnapAt, double hitMovedAt)
        {
            if (now - hitMovedAt < Steady) return Early.HitMoving;
            if (now - lastSnapAt < EarlyFloor) return Early.Deferred;
            return Early.Now;
        }
        // `barrier:`: a condition that turned true; the next starting
        // point is a barrier. barrierWas: each condition's last value, so a
        // level condition (== a boss room's screen) counts once on entry.
        bool barrierPending;
        string barrierWhy;
        bool[] barrierWas;
        // mode checkpoints: null in mode rewind
        public readonly Sequencer Seq;
        readonly Stopwatch clock = new Stopwatch();

        public int Deaths { get { return Rew.Deaths; } }

        // Single-hit kills on/off (PadSwitch: ○ off, △ on; no-ops when
        // already so). Off: a hit that costs no life is the game's own and
        // the idle counter stops; a life lost still goes back to the last
        // starting point, and snapshots, screens, checkpoints, L1 and the
        // lives byte go on (only the kill on a single hit is off: the whole
        // mode off, back on, had no starting point until the next
        // checkpoint). A game without lives: off, every hit is the game's.
        public bool On = true;
        int requested;   // 0 none, 1 off, 2 on: set by the pad threads
        public void Request(bool on) { Interlocked.Exchange(ref requested, on ? 2 : 1); }
        // L1 (PadSwitch): back to the starting point before the last, the
        // death count as it was when the player went through it (a way out
        // of a stuck spot). One press
        // per step back; nothing before the first starting point.
        int backRequested;
        public void RequestBack() { Interlocked.Exchange(ref backRequested, 1); }

        // Armed: hits count. A game's title demo plays by itself and gets
        // hurt (Super Mario Bros.' Mario loses his power-up, 3's title
        // changes suits: deaths with nobody playing, 2026-10-09). With
        // Armed false (the launcher), hits count from the player's first
        // press on a real pad, the starting point taken then; no pad report
        // within ArmAfter seconds (no DualSense, a reader failing) arms it
        // anyway: Celeste mode never stays off unseen.
        public bool Armed = true;
        public double ArmAfter = 5.0;   // the smoke test sets it out of reach
        int pressedFlag;
        double unarmedSince = -1;
        public void Pressed() { Interlocked.Exchange(ref pressedFlag, 1); }
        public PadSwitch Pads;

        public Watcher(Game game, RetroArch ra, Snapshots snaps)
        {
            this.game = game; this.ra = ra; this.snaps = snaps;
            Rew = new Rewinder(game.Rewind, game.Hold);
            if (game.UsesCheckpoints) Seq = new Sequencer(game.Checkpoints, game.CheckpointWait);
        }

        void SeqLog()
        {
            if (Seq == null) return;
            foreach (string e in Seq.Events) Log.Write("checkpoint", "what", e, "t", T);
            Seq.Events.Clear();
        }

        void NewStart()
        {
            Rew.NewStage();
            if (Seq != null) { Seq.Reset(); SeqLog(); }
        }

        // Watched bytes read in ranges, one per 256-byte page: RetroArch
        // answers one memory command a frame, so one command per address cost
        // a frame per address (2026-10-11: Mega Man 1's 20 addresses, 333 ms
        // a pass, a reload 340 ms and a stage start's 0.4 s beam-in sometimes
        // unseen). Never across a page: fceumm cut "READ_CORE_MEMORY 14 1005"
        // one byte short, at $0400 (a range from $0000 came back whole up to
        // 1024 bytes).
        List<int[]> readRanges;

        public static List<int[]> Ranges(IEnumerable<int> addrs)
        {
            var sorted = new List<int>(addrs);
            sorted.Sort();
            var out_ = new List<int[]>();
            foreach (int a in sorted)
            {
                if (out_.Count > 0 && (a >> 8) == (out_[out_.Count - 1][0] >> 8)) out_[out_.Count - 1][1] = a;
                else out_.Add(new[] { a, a });
            }
            return out_;
        }

        // The bytes at addrs, through ranges (computed by the caller), or null
        Dictionary<int, int> ReadSome(ICollection<int> addrs, List<int[]> ranges)
        {
            var d = new Dictionary<int, int>();
            foreach (int[] r in ranges)
            {
                byte[] b = ra.ReadRange(r[0], r[1] - r[0] + 1);
                if (b == null) return null;
                foreach (int a in addrs) if (a >= r[0] && a <= r[1]) d[a] = b[a - r[0]];
            }
            return d;
        }

        Dictionary<int, int> ReadAll()
        {
            if (readRanges == null)
            {
                readRanges = Ranges(game.Addrs);
                Log.Write("read_ranges", "addresses", game.Addrs.Count, "commands", readRanges.Count, "ranges", string.Join(" ", readRanges.ConvertAll(r => string.Format("{0:X4}-{1:X4}", r[0], r[1]))));
            }
            return ReadSome(game.Addrs, readRanges);
        }

        // Polls until `alive` says RetroArch is gone, or Stop is set.
        public void Run(Func<bool> alive)
        {
            string memAnswer = ra.ChooseMemory(game.Addrs.Min);
            Log.Write("memory_commands", "commands", ra.CoreRam ? "READ/WRITE_CORE_RAM" : "READ/WRITE_CORE_MEMORY", "probe", memAnswer ?? "(no answer)");
            Log.Write("watch_start", "game", game.Name, "rewind", game.Rewind, "hit_rules", game.Hit.Count, "addresses", string.Join(",", game.Addrs), "input", game.Input, "keep_ranges", game.Keep.Count);
            Dictionary<int, int> prev = null;
            clock.Start();
            double lastTick = 0, nextSnap = 1, lastStatusAt = -1;
            string status = null;
            int unreadable = 0;
            while (alive() && !Stop)
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now - lastStatusAt >= 0.25)
                {
                    string st = ra.Status();
                    if (st != status) Log.Write("status", "status", st ?? "(no answer)");
                    status = st; lastStatusAt = now;
                }
                var cur = status == "PLAYING" ? ReadAll() : null;
                if (cur == null)
                {
                    if (status == "PLAYING" && unreadable++ % 120 == 0) Log.Write("memory_unreadable");
                    lastTick = now; Thread.Sleep(16); continue;
                }
                if (prev == null) prev = cur;
                if (game.Patches.Count > 0) PadPatches(cur, prev, now);
                bool padPressed = Interlocked.Exchange(ref pressedFlag, 0) == 1;
                if (!Armed)
                {
                    if (unarmedSince < 0) unarmedSince = now;
                    string why = padPressed ? "the player pressed a button"
                        : (now - unarmedSince >= ArmAfter && (Pads == null || Pads.Reports == 0) ? "no pad report in " + ArmAfter + " s" : null);
                    if (why != null) { Armed = true; unarmedSince = -1; NewStart(); nextSnap = T; Log.Write("armed", "t", T, "why", why); }
                }
                int req = Interlocked.Exchange(ref requested, 0);
                if (req == 1 && On) { On = false; Rew.Counting = false; Log.Write("celeste_off", "t", T, "deaths", Rew.Deaths, "still_counted", game.Lives >= 0 ? "a life lost" : "nothing: no lives byte"); }
                else if (req == 2 && !On) { On = true; Log.Write("celeste_on", "t", T, "deaths", Rew.Deaths, "start_points", Rew.StartPoints.Count); }
                else if (req != 0) Log.Write("celeste_switch_noop", "on", On);
                bool backAsked = Interlocked.Exchange(ref backRequested, 0) == 1;
                // the lives too: in a one-hit game they are the only hit byte,
                // and Contra's title moves them (62, 61, 60) until the game
                // sets 02 (2026-10-09)
                foreach (int a in game.SteadyAddrs) if (cur[a] != prev[a]) { hitMovedAt = now; break; }
                // a death marker showing its value: a death under way
                foreach (int[] mk in game.Markers) if (cur[mk[0]] == mk[1]) { hitMovedAt = now; break; }
                // REWIND TO SCREEN: a new screen; its first snapshot taken
                // with the hit bytes still is the next starting point
                if (game.ScreenOn && Armed && !double.IsNaN(Rew.Checkpoint) && cur[game.Screen] != prev[game.Screen])
                {
                    screenPending = true;
                    screenChangedT = T;
                    Log.Write("screen_changed", "t", T, "screen", string.Format("{0:X2}>{1:X2}", prev[game.Screen], cur[game.Screen]));
                    // its start point at the next pass, not at the next
                    // once-a-second snapshot (0-1 s into the screen)
                    Early early = EarlySnapshot(now, lastSnapAt, hitMovedAt);
                    if (early == Early.Now && nextSnap > T) nextSnap = T;
                    Log.Write("screen_snapshot_early", "t", T, "decision", early.ToString(), "last_snapshot_s_ago", now - lastSnapAt, "hit_moved_s_ago", now - hitMovedAt);
                }
                // a barrier condition turning true: the next starting point
                // (taken even without a new screen) is a barrier
                if (game.ScreenOn && game.Barrier.Count > 0)
                {
                    if (barrierWas == null) barrierWas = new bool[game.Barrier.Count];
                    for (int bi = 0; bi < game.Barrier.Count; bi++)
                    {
                        bool holds = game.Barrier[bi](cur, prev);
                        if (holds && !barrierWas[bi] && Armed && !double.IsNaN(Rew.Checkpoint))
                        {
                            barrierPending = true; barrierWhy = game.BarrierSrc[bi];
                            Log.Write("barrier_seen", "t", T, "condition", barrierWhy);
                        }
                        barrierWas[bi] = holds;
                    }
                }
                // mode checkpoints: a checkpoint seen; its snapshot at once
                // also before the first starting point: a stage's start seen
                // while the snapshots were not ready yet was lost, and the
                // stage ran on a fallback point taken mid-hit (2026-10-11)
                if (Seq != null && Armed)
                {
                    Seq.Look(cur, prev, T);
                    // the waiting checkpoint's snapshot now, never postponed
                    if (Seq.Pending != null && double.IsNaN(Seq.Pending.SnapT) && nextSnap > T) nextSnap = T;
                    SeqLog();
                }
                double dt = now - lastTick;
                // A just-loaded snapshot can hold a pressed pad for a frame.
                bool pressed = game.Input >= 0 && cur[game.Input] != 0 && now - lastLoadAt > 0.3;
                // COUNT LIVES shows the deaths, past the game's own cap: a
                // 1-up there sets the lives back to the cap (Mega Man 6,
                // 0x54 -> 0x09, 2026-10-12: "taking a life item makes you
                // rewind"); a death takes one life, so a bigger drop is the
                // cap, no hit and no death, and the deaths are written back
                if (Armed && game.Lives >= 0 && game.WriteLives && cur[game.Lives] < prev[game.Lives] - 1)
                {
                    Log.Write("lives_capped", "t", T, "from", prev[game.Lives], "to", cur[game.Lives]);
                    prev[game.Lives] = cur[game.Lives];
                }
                bool isHit = game.IsHit(cur, prev);
                // single hits off (○): only a death still counts
                bool death = isHit && game.IsDeath(cur, prev);
                if (!On && isHit && !death)
                {
                    var why = new StringBuilder();
                    foreach (int a in game.Addrs) if (cur[a] != prev[a]) why.AppendFormat("{0:X4}:{1:X2}>{2:X2} ", a, prev[a], cur[a]);
                    Log.Write("hit_off", "t", T, "changed", why.ToString().Trim());
                    isHit = false;
                }
                // a free zone (no_rewind): a hit is the game's own; a death
                // goes back to the zone's checkpoint, as any (a death goes
                // back to the start of the fight, a hit does not)
                if (isHit && !death && Seq != null && Seq.InFree)
                {
                    var why = new StringBuilder();
                    foreach (int a in game.Addrs) if (cur[a] != prev[a]) why.AppendFormat("{0:X4}:{1:X2}>{2:X2} ", a, prev[a], cur[a]);
                    Log.Write("hit_free", "t", T, "checkpoint_index", Seq.Index, "changed", why.ToString().Trim());
                    isHit = false;
                }
                if (isHit && !Armed)
                {
                    var why = new StringBuilder();
                    foreach (int a in game.Addrs) if (cur[a] != prev[a]) why.AppendFormat("{0:X4}:{1:X2}>{2:X2} ", a, prev[a], cur[a]);
                    Log.Write("hit_unarmed", "t", T, "changed", why.ToString().Trim());
                }
                else if (isHit && double.IsNaN(Rew.Checkpoint))
                {
                    // armed, but no starting point yet: the menus are handing
                    // over to the game (Contra: title 62 -> lives 02, a
                    // "drop" at the very start, rewound into the title)
                    var why = new StringBuilder();
                    foreach (int a in game.Addrs) if (cur[a] != prev[a]) why.AppendFormat("{0:X4}:{1:X2}>{2:X2} ", a, prev[a], cur[a]);
                    Log.Write("hit_before_start", "t", T, "changed", why.ToString().Trim());
                }
                if (Armed && isHit && !double.IsNaN(Rew.Checkpoint))
                {
                    var changed = new StringBuilder();
                    foreach (int a in game.Addrs) if (cur[a] != prev[a]) changed.AppendFormat("{0:X4}:{1:X2}>{2:X2} ", a, prev[a], cur[a]);
                    bool counting = Rew.Counting;
                    double target;
                    if (Seq != null)
                    {
                        target = Rew.OnHitCheckpoint(T);
                        Seq.Hit();
                        Log.Write("hit", "deaths", Rew.Deaths, "t", T, "target", target, "rule", "checkpoint", "checkpoint_index", Seq.Index, "counter_started", !counting, "changed", changed.ToString().Trim());
                        SeqLog();
                    }
                    else if (game.ScreenOn)
                    {
                        bool stopped;
                        string rule;
                        double floorBefore = Rew.Floor;
                        target = Rew.OnHitScreen(T, game.ScreenN, out stopped, out rule);
                        Log.Write("hit", "deaths", Rew.Deaths, "t", T, "target", target, "screens_back", game.ScreenN, "rule", rule, "floor", floorBefore, "hold_s", Rew.Hold, "barrier_stop", stopped, "checkpoint", Rew.Checkpoint, "counter_started", !counting, "changed", changed.ToString().Trim());
                    }
                    else
                    {
                        target = Rew.OnHit(T);
                        Log.Write("hit", "deaths", Rew.Deaths, "t", T, "target", target, "checkpoint", Rew.Checkpoint, "counter_started", !counting, "changed", changed.ToString().Trim());
                    }
                    double hitT = T;
                    deathRewind = death;
                    cur = GoBack(target, cur);
                    now = clock.Elapsed.TotalSeconds;
                    // a failed load leaves T at the hit: no new floor then
                    if (game.ScreenOn && T < hitT) { Rew.UsedLanding(); Log.Write("floor", "t", Rew.Floor); }
                    nextSnap = T + 1;
                }
                else if (backAsked && Armed && !double.IsNaN(Rew.Checkpoint))
                {
                    Rewinder.StartPoint sp;
                    if (!Rew.Previous(out sp)) Log.Write("back_noop", "t", T, "why", "no starting point before this one", "start_points", Rew.StartPoints.Count);
                    else
                    {
                        int before = Rew.Deaths;
                        double from = Rew.Checkpoint;
                        // mode checkpoints keeps the lives: the deaths stay, and the game's own lives
                        // byte is written back when COUNT LIVES is off; mode
                        // rewind still takes the deaths back to that point
                        bool keepLives = Seq != null;
                        int livesBefore = game.Lives >= 0 ? cur[game.Lives] : -1;
                        if (!keepLives) Rew.Deaths = sp.Deaths;
                        Rew.Counting = false;
                        deathRewind = false;
                        cur = GoBack(sp.T, cur);
                        now = clock.Elapsed.TotalSeconds;
                        bool landed = Rew.Checkpoint < from - 1e-6;
                        if (!landed) Rew.Deaths = before;
                        else if (keepLives && !game.WriteLives && livesBefore >= 0 && cur[game.Lives] != livesBefore)
                        {
                            ra.Write(game.Lives, livesBefore);
                            cur[game.Lives] = livesBefore;
                        }
                        Log.Write(landed ? "back" : "back_failed", "t", T, "from", from, "to", sp.T, "landed_on", Rew.Checkpoint, "deaths_before", before, "deaths", Rew.Deaths, "start_points", Rew.StartPoints.Count);
                        nextSnap = T + 1;
                    }
                }
                else if (backAsked) Log.Write("back_noop", "t", T, "why", !Armed ? "not armed" : "no starting point yet");
                // the idle counter's escape is mode rewind's way out of a
                // death loop; mode checkpoints has none (it sent Cut Man 5
                // back to the stage's start after 10 s without a press)
                else if (Seq == null && Armed && On && Rew.Tick(dt, pressed))
                {
                    int before = Rew.Deaths;
                    double target = Rew.Escape();
                    // REWIND TO SCREEN: not past the last barrier either
                    double barrierT = game.ScreenOn ? Rew.LastBarrier() : double.NaN;
                    bool held = !double.IsNaN(barrierT) && target < barrierT;
                    if (held) target = barrierT;
                    Log.Write("escape", "t", T, "target", target, "barrier_stop", held, "deaths_before", before, "deaths", Rew.Deaths);
                    deathRewind = false;
                    cur = GoBack(target, cur);
                    now = clock.Elapsed.TotalSeconds;
                    nextSnap = T + 1;
                }
                else
                {
                    T += dt;
                    // mode checkpoints saves only at checkpoints: no starting
                    // point of its own
                    // before the first one counts; until then a hit is the
                    // game's own
                    if (T >= nextSnap && double.IsNaN(Rew.Checkpoint) && Seq == null && now - hitMovedAt < Steady)
                    {
                        var held = new StringBuilder();
                        foreach (int a in game.HitAddrs) held.AppendFormat("{0:X4}:{1:X2} ", a, cur[a]);
                        Log.Write("start_point_wait", "t", T, "hit_moved_s_ago", now - hitMovedAt, "hit_bytes", held.ToString().Trim());
                        nextSnap = T + 0.25;
                    }
                    else if (T >= nextSnap)
                    {
                        // mode checkpoints: a checkpoint waiting for its
                        // snapshot gets it now
                        bool forCheckpoint = Seq != null && Seq.Pending != null && double.IsNaN(Seq.Pending.SnapT);
                        bool took = TakeSnapshot(cur, forCheckpoint);
                        if (took) lastSnapAt = now;
                        if (took && double.IsNaN(Rew.Checkpoint) && Seq == null) { Rew.Landed(T); PinStartPoints(); snaps.DropBefore(T); Log.Write("start_point", "t", T, "snapshots", snaps.Ring.Count); }
                        else if (took && forCheckpoint && Seq.Snapped(T)) SeqLog();
                        else if (took && (screenPending || barrierPending) && now - hitMovedAt >= Steady)
                        {
                            bool isBarrier = barrierPending;
                            string why = barrierWhy;
                            screenPending = false; barrierPending = false; barrierWhy = null;
                            Rew.Landed(T, isBarrier, why); PinStartPoints();
                            Log.Write("screen_start", "t", T, "screen", cur[game.Screen], "barrier", isBarrier, "condition", why ?? "", "delay_s", double.IsNaN(screenChangedT) || T < screenChangedT ? -1 : T - screenChangedT, "deaths", Rew.Deaths, "start_points", Rew.StartPoints.Count);
                            screenChangedT = double.NaN;
                        }
                        nextSnap = T + (took ? 1 : 0.25);
                    }
                    // mode checkpoints: a candidate whose wait went by
                    // without a hit counts: the last starting point now
                    if (Seq != null)
                    {
                        Sequencer.Candidate c = Seq.Validate(T);
                        if (c != null)
                        {
                            Rew.Landed(c.SnapT, false, c.Name); PinStartPoints();
                            Log.Write("checkpoint_start", "t", c.SnapT, "name", c.Name, "deaths", Rew.Deaths, "start_points", Rew.StartPoints.Count);
                        }
                        SeqLog();
                    }
                }
                // not before the starting point: the menus may use that byte
                if (Armed && game.Lives >= 0 && game.WriteLives && !double.IsNaN(Rew.Checkpoint))
                {
                    int shown = Shown(Rew.Deaths, game.LivesFloor);
                    if (cur[game.Lives] != shown) { ra.Write(game.Lives, shown); cur[game.Lives] = shown; }
                }
                prev = cur; lastTick = now;
                Thread.Sleep(8);
            }
            Log.Write("watch_end", "deaths", Rew.Deaths, "t", T);
        }

        // ---- the rewind: the game frozen from the music read to the music write
        //
        // Every step is checked, and a failed one never loops:
        // 1. freeze RetroArch (PAUSE_TOGGLE, until GET_STATUS says PAUSED): a
        //    loaded state no longer runs into a hit, and the music read now is
        //    the music written back after (2026-10-09: writes into a running
        //    game, and writes after a load that had not landed, broke it);
        // 2. read the `keep` ranges;
        // 3. put the snapshot in the slot (its exact bytes checked) and load;
        // 4. a load sent is a load landed: the snapshot is never judged by
        //    its recorded values (after Bomb Man's 75 % checkpoint was
        //    thrown away: its value of Mega Man's
        //    animation byte, read a frame off the save, 03 for the save's 06;
        //    in 2080 loads since 2026-10-09 the comparison failed 4 times,
        //    each one byte that flickers, never a load that did not happen).
        //    The comparison only times the wait: done as soon as the values
        //    match, else after LandWaitMs, logged `verified: false`. Only a
        //    load that could not be sent (the slot copy) tries again, then
        //    the next older snapshot, three at most;
        // 5. after the landing: the music written back and read back;
        // 6. unfreeze (PLAYING checked; toggled once more if still paused).
        // Nothing landed: the game goes on where it was, with a fresh reading
        // for the next hit test (the same hit is never read twice).
        public const int LoadTries = 2, SnapshotTries = 3, LandWaitMs = 300;

        bool Freeze()
        {
            ra.Send("PAUSE_TOGGLE");
            var w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < 400)
            {
                if (ra.Status() == "PAUSED") return true;
                Thread.Sleep(5);
            }
            // not paused: maybe the toggle never arrived, maybe it arrives
            // late; leave the game as it is either way
            string st = ra.Status();
            Log.Write("freeze_failed", "status", st ?? "(no answer)");
            if (st == "PAUSED") return true;
            return false;
        }

        void Unfreeze()
        {
            for (int toggle = 0; toggle < 2; toggle++)
            {
                ra.Send("PAUSE_TOGGLE");
                var w = Stopwatch.StartNew();
                while (w.ElapsedMilliseconds < 400)
                {
                    string st = ra.Status();
                    if (st == "PLAYING") { if (toggle > 0) Log.Write("unfreeze_retried"); return; }
                    if (st == null) break;
                    Thread.Sleep(5);
                }
            }
            Log.Write("unfreeze_failed", "status", ra.Status() ?? "(no answer)");
        }

        // The watched bytes a landing is judged on: all but the pad (a person
        // presses it) and the lives (written by us).
        bool Matches(Dictionary<int, int> want, Dictionary<int, int> got, out string diff, HashSet<int> skip = null)
        {
            diff = null;
            if (got == null) { diff = "unreadable"; return false; }
            foreach (int a in game.Addrs)
            {
                if (a == game.Input || a == game.Lives) continue;
                if (!want.ContainsKey(a) || (skip != null && skip.Contains(a))) continue;
                if (got[a] != want[a]) { diff = string.Format("{0:X4}:{1:X2}!={2:X2}", a, got[a], want[a]); return false; }
            }
            return true;
        }

        bool WaitLanded(Snapshots.Snap s, out string diff, out long ms)
        {
            var w = Stopwatch.StartNew();
            diff = null;
            while (w.ElapsedMilliseconds < LandWaitMs)
            {
                Thread.Sleep(10);
                if (Matches(s.Values, ReadAll(), out diff, s.Unsure)) { ms = w.ElapsedMilliseconds; return true; }
            }
            ms = w.ElapsedMilliseconds;
            return false;
        }

        // set by the hit that asks for the rewind: a death (lives down or a
        // `death` condition), whose music is never carried
        bool deathRewind;

        Dictionary<int, int> GoBack(double target, Dictionary<int, int> cur)
        {
            if (snaps.SlotFile == null) { Log.Write("no_snapshot", "target", target, "why", "no slot file yet"); return cur; }
            bool frozen = Freeze();
            if (!frozen) { Log.Write("rewind_skipped", "target", target, "why", "could not freeze"); return ReadAll() ?? cur; }
            Snapshots.Snap landedOn = null;
            var patched = new Dictionary<int, int>();
            try
            {
                var kept = new List<byte[]>();
                foreach (int[] r in game.Keep) kept.Add(ra.ReadRange(r[0], r[1] - r[0] + 1));
                var songNow = new Dictionary<int, int>();
                foreach (int a in game.Song) songNow[a] = ra.Read(a);
                for (int n = 0; n < SnapshotTries && landedOn == null; n++)
                {
                    Snapshots.Snap s = snaps.Pick(target);
                    if (s == null) { Log.Write("no_snapshot", "target", target, "snapshots", snaps.Ring.Count); break; }
                    if (s.Values == null) { Log.Write("snapshot_without_values", "snapshot", s.T); snaps.Discard(s); continue; }
                    for (int attempt = 1; attempt <= LoadTries && landedOn == null; attempt++)
                    {
                        string diff = null; long ms = 0;
                        bool sent = snaps.Load(ra, s);
                        bool verified = sent && WaitLanded(s, out diff, out ms);
                        Log.Write("load_attempt", "snapshot", s.T, "attempt", attempt, "sent", sent, "landed", sent, "verified", verified, "after_ms", ms, "diff", diff ?? "");
                        if (sent) landedOn = s;
                    }
                    if (landedOn == null)
                    {
                        snaps.Discard(s);
                        Log.Write("load_failed", "snapshot", s.T, "snapshots", snaps.Ring.Count);
                    }
                }
                if (landedOn == null) return ReadAll() ?? cur;
                // the same song now and in the snapshot, or no song bytes: the
                // music goes on; another song: the snapshot keeps its own
                string songNote = "";
                // a death's rewind never carries the music: the game has
                // started its death sound by the time the lives drop (Mega
                // Man 6's pit, 2026-10-12: the jingle's state carried into
                // the stage, "a bad sound glitch after dying in a pit")
                bool sameSong = !deathRewind;
                foreach (int a in game.Song)
                {
                    songNote += string.Format("{0:X4}:{1:X2}/{2:X2} ", a, songNow[a] & 0xFF, landedOn.Values[a]);
                    if (game.SongBits(a, songNow[a]) != game.SongBits(a, landedOn.Values[a])) sameSong = false;
                }
                int keptOk = 0;
                for (int i = 0; sameSong && i < game.Keep.Count; i++)
                {
                    if (kept[i] == null) continue;
                    ra.WriteRange(game.Keep[i][0], kept[i]);
                    byte[] back = ra.ReadRange(game.Keep[i][0], kept[i].Length);
                    if (back != null && Same(back, kept[i])) keptOk++;
                    else Log.Write("keep_not_written", "range", string.Format("{0:X4}-{1:X4}", game.Keep[i][0], game.Keep[i][1]));
                }
                if (game.Lives >= 0 && game.WriteLives) ra.Write(game.Lives, Shown(Rew.Deaths, game.LivesFloor));
                T = landedOn.T;
                Rew.Landed(landedOn.T);
                if (Seq != null) { Seq.LandedAt(landedOn.T, landedOn.Values); SeqLog(); ApplyPatches(landedOn.T, patched); }
                PinStartPoints();
                Log.Write("rewound", "to", landedOn.T, "deaths", Rew.Deaths, "kept_ranges", keptOk, "of", game.Keep.Count, "music", game.Keep.Count == 0 ? "-" : (sameSong ? "carried" : (deathRewind ? "the snapshot's own: a death" : "the snapshot's own: another song")), "song_now_snapshot", songNote.Trim());
            }
            finally { Unfreeze(); }
            lastLoadAt = clock.Elapsed.TotalSeconds;
            var baseline = new Dictionary<int, int>(landedOn.Values);
            if (game.Lives >= 0 && game.WriteLives) baseline[game.Lives] = Shown(Rew.Deaths, game.LivesFloor);
            foreach (KeyValuePair<int, int> kv in patched) if (baseline.ContainsKey(kv.Key)) baseline[kv.Key] = kv.Value;
            return baseline;
        }

        // The landed checkpoint's patches, written while the game is still
        // frozen; what was written, by address (the next read's baseline).
        void ApplyPatches(double t, Dictionary<int, int> patched)
        {
            CheckpointDef d = Seq.CountedAt(t);
            if (d == null) return;
            foreach (Patch p in d.Patches)
            {
                int from = 0;
                if (p.From >= 0)
                {
                    byte[] b = ra.ReadRange(p.From, 1);
                    if (b == null || b.Length < 1) { Log.Write("checkpoint_patch_failed", "name", d.Name, "patch", p.Src, "why", "unreadable"); continue; }
                    from = b[0];
                }
                int v = p.ValueFrom(from);
                ra.Write(p.Addr, v);
                patched[p.Addr] = v;
                Log.Write("checkpoint_patch", "name", d.Name, "patch", p.Src, "addr", string.Format("{0:X4}", p.Addr), "value", v);
            }
        }

        // `patches`: a press seen, or a startup patch's byte off: written now,
        // into the game and into this read (the rest of the pass sees them)
        void PadPatches(Dictionary<int, int> cur, Dictionary<int, int> prev, double now)
        {
            // The patches one press triggers are all judged on one read made
            // before any of them writes: Mega Man 3's Select toggles $0060
            // (0x09 Doc Robot, 0x12 Break Man) with two patches whose `when`
            // read it, and a read per patch let the second see the first's
            // write and undo it (Mega Man 3's second Select).
            var pressedOnes = new List<PadPatch>();
            foreach (PadPatch pp in game.Patches) if (pp.Pressed(cur, prev, game.Input)) pressedOnes.Add(pp);
            Dictionary<int, int> atPress = null;
            if (pressedOnes.Count > 0)
            {
                var union = new SortedSet<int>();
                foreach (PadPatch pp in pressedOnes) union.UnionWith(pp.Addrs);
                atPress = ReadSome(union, Ranges(union));
                if (atPress == null) Log.Write("patch_unreadable", "names", string.Join(", ", pressedOnes.ConvertAll(x => x.Name)));
            }
            foreach (PadPatch pp in game.Patches)
            {
                bool pressed = pressedOnes.Contains(pp);
                bool check = pp.Startup && now - pp.CheckedAt >= 1.0;
                if (!pressed && !check) continue;
                if (check) pp.CheckedAt = now;
                Dictionary<int, int> vals = pressed ? atPress : ReadSome(pp.Addrs, pp.Ranges);
                if (vals == null) { if (!pressed) Log.Write("patch_unreadable", "name", pp.Name); continue; }
                if (!pp.WhenHolds(vals))
                {
                    if (pressed) Log.Write("patch_not_here", "name", pp.Name, "when", pp.WhenSrc);
                    continue;
                }
                string why = pressed ? "button" : (pp.Off(vals) ? "startup" : null);
                if (why == null) continue;
                var done = new List<string>();
                foreach (Patch w in pp.Writes)
                {
                    int v = w.ValueFrom(w.From >= 0 ? vals[w.From] : 0);
                    ra.Write(w.Addr, v);
                    if (cur.ContainsKey(w.Addr)) cur[w.Addr] = v;
                    done.Add(string.Format("{0:X4}={1:X2}", w.Addr, v));
                }
                pp.Applied++;
                // a kept patch can be rewritten often (energy the game spends):
                // its first writes and then one in a hundred
                if (why == "button" || pp.Applied <= 3 || pp.Applied % 100 == 0)
                    Log.Write("patch_applied", "name", pp.Name, "trigger", why, "times", pp.Applied, "t", T, "writes", string.Join(" ", done));
            }
        }

        void PinStartPoints()
        {
            snaps.Pinned.Clear();
            foreach (Rewinder.StartPoint sp in Rew.StartPoints) snaps.Pinned.Add(sp.T);
        }

        // A snapshot, kept only when the watched bytes (but the pad) read the
        // same before and after it was taken: the save happens somewhere in
        // between, so its values are then known exactly, and a landing can
        // be judged on all of them. A snapshot taken as a hit lands (health
        // moving) is never kept. False: none taken, try again soon.
        // keepMoving (a checkpoint's snapshot: a checkpoint is saved when
        // it is reached, never postponed): kept
        // anyway, the bytes that moved out of its landing check.
        bool TakeSnapshot(Dictionary<int, int> cur, bool keepMoving = false)
        {
            Snapshots.Snap s = snaps.Take(ra, T, cur);
            if (s == null) return false;
            string diff;
            Dictionary<int, int> after = ReadAll();
            if (!Matches(cur, after, out diff))
            {
                if (keepMoving && after != null)
                {
                    foreach (int a in game.Addrs) if (after[a] != cur[a]) s.Unsure.Add(a);
                    Log.Write("snapshot_moving_kept", "t", T, "diff", diff, "unsure", s.Unsure.Count);
                    return true;
                }
                snaps.Discard(s);
                Log.Write("snapshot_moving", "t", T, "diff", diff);
                return false;
            }
            return true;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // The death count as written into the lives byte: never 0. A game
        // whose lives read 0 takes the next death for a game over and leaves
        // the byte at 0: no drop, the death unseen, and in a one-hit game
        // (Contra, Adventure Island) the lives are the only death signal
        // (2026-10-09). Never below the game's floor (`lives_floor`, default
        // 1). Capped at 99 (two digits).
        public static int Shown(int deaths, int floor) { return Math.Max(floor, Math.Min(deaths, 99)); }

        // The ○/△ switch over every DualSense; Dispose stops it.
        public PadSwitch StartPadSwitch() { return StartPadSwitch(null); }

        // sdlDll: RetroArch's SDL2.dll, for every pad that is no DualSense
        public PadSwitch StartPadSwitch(string sdlDll)
        {
            Keys keys = Keys.For(game.RomSystem);
            Log.Write("pad_keys", "system", game.RomSystem, "off", keys.OffName, "on", keys.OnName, "back", keys.BackName);
            var sw = new PadSwitch(keys, Request, Pressed, RequestBack);
            sw.Start();
            if (sdlDll != null) { sw.Sdl = new SdlPads(sw, sdlDll); sw.Sdl.Start(); }
            Pads = sw;
            return sw;
        }

        // For PowerShell, which cannot hand a script block to another thread:
        // the loop until that RetroArch process exits, here or on a new thread.
        public void RunUntilExit(Process retroarch) { Run(() => !retroarch.HasExited); }

        public Thread StartUntilExit(Process retroarch)
        {
            var th = new Thread(() => RunUntilExit(retroarch)) { IsBackground = true };
            th.Start();
            return th;
        }
    }
}
