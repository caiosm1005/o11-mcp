// Ported from a team's Model API layout lambda (2026-11-22), then maintained here. The layout of
// OutSystems-style logic flows: nodes on Service Studio's grid, comment notes wrapped and placed.
// It knows nothing of Service Studio: it reads and writes the Flow model (Flow.cs).
// How it works and how to change it: bridge/LAYOUT.md.
#nullable disable
#pragma warning disable CS0162, CS0219, CS8321
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceStudioBridge.Layout
{
    public static class FlowLayout
    {
        /// <summary>
        /// Lays out the flows (those that only names, by path or name, or all of them when it is
        /// empty), changing the nodes' X, Y and the notes' Text in place, and says what changed.
        /// </summary>
        public static LayoutReport Arrange(IList<Flow> flows, string[] only)
        {
            // Lays out every node of a logic flow on Service Studio's grid, and wraps and
            // places its comment notes. Authors write notes as one-liners.
            // How it works and how to change it: bridge/LAYOUT.md.

            // ---- Algorithm -------------------------------------------------------------
            // The grid is Service Studio's own snap grid: 12800/7 (about 1828.57) per column and per row.
            // Rows are counted in half rows: every node of the flow sits on a whole row (an even
            // half row), except the nodes of a loop's ring drawn at half height or centred (3a).
            // Columns stay whole: a split If's branches (3b) are drawn half a column left of theirs.
            var X0 = 3200; var PITCH_N = 12800; var PITCH_D = 7;
            var START_X = 3196; var START_Y = 800;     // Start stays where Service Studio creates it
            var ROOT_GAP = 2;                // columns between the main flow and each exception handler flow
            var OWN_PUSH = 6;                // rows a node's own connectors may push it down, at most
            // Comment notes. Service Studio shows a note with exactly the line breaks its
            // text has and cuts a longer line off with "...". A note's position is its
            // top-left corner: it grows right by CHAR_W per character of its longest line
            // and down by LINE_H per line. A node's position is its icon's top-left
            // corner, and a note's link is drawn from the note's centre to the icon's. A
            // short label goes in its flow's left margin, centred on the nodes it is
            // linked to; every other note settles like a body in a physical simulation:
            // pulled towards the nodes it is linked to, kept MARGIN away from every node,
            // with its links clear of every other node, and pushed apart from other notes.
            var LINE_MAX = 100;              // longest line of a note, in characters: its text is unwrapped, then wrapped at this length
            var CHAR_W = 68; var LINE_H = 175;         // size of a note: NOTE_PAD_X + CHAR_W per character of its longest line,
            var NOTE_PAD_X = 320; var NOTE_PAD_Y = 320; // by NOTE_PAD_Y + LINE_H per line
            var ICON = 430;                  // a node's icon, from its position (its top-left corner) right and down
            var NODE_L = 800; var NODE_R = 800; var NODE_T = 750; var NODE_B = 250;   // a node's footprint around its icon's centre: the icon and its label above it
            var MARGIN = 400;                // no node within this distance of a note
            var CLEAR = 150;                 // a note's link keeps this far from every node but its own (icon and label), and a note this far from every connector
            var BIG = 1L << 40;              // an open end
            var NOTE_SPACE = 250;            // between two notes
            var TIGHT = 16;                  // pull of a link towards the note's nearest edge, against 1 towards its centre
            var PULL = 8;                    // each step a note moves 1/PULL of the way back to where its links pull it
            var SIM_STEPS = 400; var CONTACT_PASSES = 16;   // steps of the simulation; contact passes per step
            var LABEL_GAP = 2500; var LABEL_MAX = 20;  // one-line notes of up to LABEL_MAX characters go LABEL_GAP left of their flow's first column
            // Energy of a For Each loop's shape (lower is better), per connector unless said otherwise.
            var LATERAL = 100;               // per column it spans
            var LONG = 300; var STRAIGHT = 100;        // per cell beyond the next one: slanted; straight along a row or column
            var OFFAXIS = 150;               // when it is neither straight nor an exact diagonal
            var LONGBACK = 100; var BACKCAP = 4;       // per cell beyond the next one for the connector back to a For Each, up to BACKCAP cells
            var UP = 300; var CLIMB = 150;   // per row it goes up: when leaving the loop; inside the loop
            var DIVE = 150;                  // when the For Each's Cycle connector goes down: a loop starts upwards
            var FORKDIAG = 300;              // when it leaves or enters an If or Switch diagonally
            var LEAF = 300;                  // to an end node, or along a chain that ends separately, when not one cell to the right
            var BEND = 25;                   // per pair of consecutive loop connectors, times the square of the turn (in 45 degree steps)
            var CROSS = 400;                 // per pair of connectors that cross
            var PRESSURE = 40; var AREACAP = 6;        // reward per half cell of area a cycle encloses, up to AREACAP: inflates the loop
            var FAR = 300; var BEAM = 80;    // search: penalty per cell a node strays beyond reach; candidates kept per step
            // Cells tried for a node, relative to a placed neighbour: the 8 around it and 2 away along its row or column.
            var STEPS = new[] { -1, 0, 1 }.SelectMany(dy => new[] { -1, 0, 1 }.Select(dx => new[] { dx, dy })).Concat(new[] { new[] { -2, 0 }, new[] { 2, 0 }, new[] { 0, -2 }, new[] { 0, 2 } }).ToList();

            int Abs(int v) { return v < 0 ? -v : v; }
            int Min(int a, int b) { return a < b ? a : b; }
            int Sign(int v) { return v > 0 ? 1 : v < 0 ? -1 : 0; }
            // Direction of a connector as one of 8 compass steps, clockwise from north.
            int Dir(int dx, int dy) { return Sign(dx) == 0 ? (dy < 0 ? 0 : 4) : Sign(dx) > 0 ? 2 + Sign(dy) : 6 - Sign(dy); }
            // True when the cell (cx, cy) lies on the straight connector between two cells.
            bool Through(int ax, int ay, int bx, int by, int cx, int cy) {
                if ((cx == ax && cy == ay) || (cx == bx && cy == by)) return false;
                if ((bx - ax) * (cy - ay) - (by - ay) * (cx - ax) != 0) return false;
                return (cx - ax) * (cx - bx) <= 0 && (cy - ay) * (cy - by) <= 0;
            }
            int Side(int ax, int ay, int bx, int by, int cx, int cy) { return Sign((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)); }
            bool Crosses(int ax, int ay, int bx, int by, int cx, int cy, int dx, int dy) {
                if ((ax == cx && ay == cy) || (ax == dx && ay == dy) || (bx == cx && by == cy) || (bx == dx && by == dy)) return false;
                return Side(ax, ay, bx, by, cx, cy) * Side(ax, ay, bx, by, dx, dy) < 0 && Side(cx, cy, dx, dy, ax, ay) * Side(cx, cy, dx, dy, bx, by) < 0;
            }
            // True when the cell (c, q) is on the straight connector between two
            // cells, or less than half a cell from it along its own column or row
            // (rows in half rows: half a cell is one of them).
            bool Near(int c1, int r1, int c2, int r2, int c, int q) {
                if ((c - c1) * (c - c2) < 0 && Abs((r2 - r1) * (c - c1) - (q - r1) * (c2 - c1)) < Abs(c2 - c1)) return true;
                return (q - r1) * (q - r2) < 0 && 2 * Abs((c2 - c1) * (q - r1) - (c - c1) * (r2 - r1)) < Abs(r2 - r1);
            }
            // One paragraph of a note, wrapped at LINE_MAX characters. It breaks at a space,
            // outside double quotes when that keeps the line at least half full, so a
            // quoted suggestion stays in one piece. Continuation lines are indented like
            // the line's own text: past its leading spaces and a "- ", "* " or "1. "
            // marker. A word longer than a whole line is never split.
            string[] WrapLine(string s) {
                if (s.Length <= LINE_MAX) return new[] { s };
                var mark = 0; while (mark < s.Length && s[mark] == ' ') mark++;
                if (mark + 1 < s.Length && (s[mark] == '-' || s[mark] == '*') && s[mark + 1] == ' ') mark += 2;
                else {
                    var d = mark; while (d < s.Length && s[d] >= '0' && s[d] <= '9') d++;
                    if (d > mark && d + 1 < s.Length && (s[d] == '.' || s[d] == ')') && s[d + 1] == ' ') mark = d + 2;
                }
                var hang = mark <= LINE_MAX / 4 ? new string(' ', mark) : "";
                var quoted = new bool[s.Length + 1];                 // quoted[i]: an odd number of double quotes before i
                for (var i = 0; i < s.Length; i++) quoted[i + 1] = s[i] == (char)34 ? !quoted[i] : quoted[i];
                var wrapped = new string[0].ToList();
                var pos = 0; var prefix = "";
                while (true) {
                    var room = LINE_MAX - prefix.Length;
                    if (s.Length - pos <= room) { wrapped.Add(prefix + s.Substring(pos)); break; }
                    var cut = -1;
                    for (var b = pos + room; b > pos && cut < 0; b--) if (s[b] == ' ' && b > mark && !quoted[b] && 2 * (b - pos) >= room) cut = b;
                    for (var b = pos + room; b > pos && cut < 0; b--) if (s[b] == ' ' && b > mark) cut = b;
                    for (var b = pos + room + 1; b < s.Length && cut < 0; b++) if (s[b] == ' ') cut = b;
                    if (cut < 0) { wrapped.Add(prefix + s.Substring(pos)); break; }
                    wrapped.Add(prefix + s.Substring(pos, cut - pos).TrimEnd(' '));
                    pos = cut; while (pos < s.Length && s[pos] == ' ') pos++;
                    if (pos >= s.Length) break;
                    prefix = hang;
                }
                return wrapped.ToArray();
            }
            // A paragraph line of a note: the leading indent kept on its first line and
            // dropped on the lines joined to it, tabs as spaces, runs of spaces as one
            // (so that wrapping its own output gives back the same text).
            string Squeeze(string row, bool first) {
                var lead = 0; while (lead < row.Length && (row[lead] == ' ' || row[lead] == (char)9)) lead++;
                var words = row.Substring(lead).Replace((char)9, ' ').Split(' ').Where(w => w.Length > 0);
                return (first ? new string(' ', lead) : "") + string.Join(" ", words);
            }
            // A note's text, unwrapped and then wrapped. Every line is trimmed at the
            // end. A single line break is unwrapped: the lines around it are joined
            // with a space. Only two or more line breaks in a row (an empty line) are
            // kept, as the break between paragraphs; empty lines at the start and end
            // go. Then every paragraph is wrapped at LINE_MAX. Running it on its own
            // output changes nothing.
            string WrapText(string text) {
                var crlf = ((char)13).ToString() + (char)10;
                var rows = text.Split((char)10).Select(l => l.TrimEnd()).ToList();
                while (rows.Count > 0 && rows[0].Length == 0) rows.RemoveAt(0);
                while (rows.Count > 0 && rows[rows.Count - 1].Length == 0) rows.RemoveAt(rows.Count - 1);
                var result = new string[0].ToList();
                string paragraph = null;
                foreach (var row in rows) {
                    if (row.Length == 0) {
                        if (paragraph != null) { result.AddRange(WrapLine(paragraph)); paragraph = null; }
                        result.Add("");
                    }
                    else paragraph = paragraph == null ? Squeeze(row, true) : paragraph + " " + Squeeze(row, false);
                }
                if (paragraph != null) result.AddRange(WrapLine(paragraph));
                return string.Join(text.Contains(crlf) ? crlf : ((char)10).ToString(), result);
            }
            // Cost along one axis of a note at lo..lo+size, linked to points given doubled
            // (a2 = 2 * position, so a centre stays an integer): k * TIGHT per squared
            // distance from each point to the note, plus the squared distance from the
            // point to the note's centre. With k = 0 and one point, it is the squared
            // distance a note moves from where it was.
            long AxisCost(int[] a2, int k, int lo, int size) {
                long cost = 0;
                foreach (var a in a2) {
                    long d = a < 2L * lo ? 2L * lo - a : a > 2L * (lo + size) ? a - 2L * (lo + size) : 0;
                    long c = a - 2L * lo - size;
                    cost += k * TIGHT * d * d + c * c;
                }
                return cost;
            }
            // The position along one axis with the lowest cost (the cost is convex).
            int AxisBest(int[] a2, int k, int size) {
                int lo = 0, hi = 1 << 20;
                while (hi - lo > 2) {
                    var m1 = lo + (hi - lo) / 3; var m2 = hi - (hi - lo) / 3;
                    if (AxisCost(a2, k, m1, size) <= AxisCost(a2, k, m2, size)) hi = m2; else lo = m1;
                }
                var best = lo;
                for (var v = lo; v <= hi; v++) if (AxisCost(a2, k, v, size) < AxisCost(a2, k, best, size)) best = v;
                return best;
            }
            // True when a note's top-left corner (x, y) is in none of the blocked areas:
            // open rectangles x1 < x < x2, y1 < y < y2 of top-left corners.
            bool Free(int x, int y, int[][] blocked) {
                return x >= 0 && y >= 0 && !blocked.Any(b => b[0] < x && x < b[1] && b[2] < y && y < b[3]);
            }
            long FloorDiv(long a, long b) { var q = a / b; return q * b != a && (a < 0) != (b < 0) ? q - 1 : q; }
            long CeilDiv(long a, long b) { return -FloorDiv(-a, b); }
            // True when the segment between two points misses the box { x1, y1, x2, y2 }; all doubled.
            bool Misses(long ax, long ay, long bx, long by, long[] box) {
                if (ax < box[0] && bx < box[0] || ax > box[2] && bx > box[2] || ay < box[1] && by < box[1] || ay > box[3] && by > box[3]) return true;
                long sx = bx - ax, sy = by - ay;
                var c = new[] { sx * (box[1] - ay) - sy * (box[0] - ax), sx * (box[1] - ay) - sy * (box[2] - ax), sx * (box[3] - ay) - sy * (box[0] - ax), sx * (box[3] - ay) - sy * (box[2] - ax) };
                return c.All(v => v > 0) || c.All(v => v < 0);
            }
            // The rows (top y) from which the link of a w by h note whose left edge is at
            // x, drawn from the note's centre to the point e, would run through the box
            // { x1, y1, x2, y2 } (e and box doubled): an open interval { below, above }
            // of rows, or null when no row would.
            long[] LinkRows(int x, int w, int h, long[] e, long[] box) {
                long cx = 2L * x + w, px = e[0], py = e[1];
                long left = cx < px ? cx : px, right = cx < px ? px : cx;
                long bx1 = box[0] > left ? box[0] : left, bx2 = box[2] < right ? box[2] : right;
                if (bx1 > bx2) return null;
                long lo, hi;
                if (cx == px) { if (box[1] > py) { lo = box[1]; hi = BIG; } else { lo = -BIG; hi = box[3]; } }
                else {
                    // d from the point along x, the link is within the box's rows for a note
                    // centre (cy) between py + (y1 - py) * span / d and py + (y2 - py) * span / d.
                    long span = cx > px ? cx - px : px - cx;
                    long near = cx > px ? bx1 - px : px - bx2, far = cx > px ? bx2 - px : px - bx1;
                    lo = BIG; hi = -BIG;
                    foreach (var d in new[] { near, far }) {
                        var a = d == 0 ? (box[1] < py ? -BIG : BIG) : py + FloorDiv((box[1] - py) * span, d);
                        var b = d == 0 ? (box[3] > py ? BIG : -BIG) : py + CeilDiv((box[3] - py) * span, d);
                        if (a < lo) lo = a;
                        if (b > hi) hi = b;
                    }
                }
                return new[] { lo <= -BIG ? -BIG : FloorDiv(lo - h, 2) - 1, hi >= BIG ? BIG : CeilDiv(hi - h, 2) + 1 };
            }
            // The rows (top y) at which a w by h note whose left edge is at x would come
            // within CLEAR of the connector { x1, y1, x2, y2 } (doubled): an open
            // interval { below, above } of rows, or null when none would.
            long[] LineRows(int x, int w, int h, long[] line) {
                long l = 2L * (x - CLEAR), r = 2L * (x + w + CLEAR);
                long ax = line[0], ay = line[1], bx = line[2], by = line[3];
                if (ax > bx) { ax = line[2]; ay = line[3]; bx = line[0]; by = line[1]; }
                if (bx < l || ax > r) return null;
                long lo = ay < by ? ay : by, hi = ay < by ? by : ay;
                if (ax != bx) {
                    long cl = ax > l ? ax : l, cr = bx < r ? bx : r;
                    var yl = ay * (bx - ax) + (by - ay) * (cl - ax); var yr = ay * (bx - ax) + (by - ay) * (cr - ax);
                    lo = FloorDiv(yl < yr ? yl : yr, bx - ax); hi = CeilDiv(yl < yr ? yr : yl, bx - ax);
                }
                return new[] { FloorDiv(lo, 2) - h - CLEAR - 1, CeilDiv(hi, 2) + CLEAR + 1 };
            }
            // Where a w by h note comes to rest: the free top-left corner with the lowest
            // cost, as { x, y, tier }. The cost is separate along each axis, so for each
            // x worth trying (the best position along x, or the edge of a blocked area)
            // the best y is the best position along y, or an edge of the rows shut in
            // that column: blocked areas, the rows where the note would lie on a
            // connector (wires), and the rows from which one of the note's links (ends,
            // doubled) would run through a box it must miss (avoid, per link). Tier 0
            // keeps clear of all of them; when no place does, tier 1 lets the links
            // cross a node, and tier 2 is merely free.
            int[] Settle(int w, int h, int[] ax2, int[] ay2, int k, int[][] blocked, long[][] ends, long[][][] avoid, long[][] wires) {
                var xs = new[] { AxisBest(ax2, k, w), 0 }.Concat(blocked.SelectMany(b => new[] { b[0], b[1] })).Where(v => v >= 0).Distinct().OrderBy(v => v).ToArray();
                var best = AxisBest(ay2, k, h);
                for (var pass = ends.Length > 0 ? 0 : wires.Length > 0 ? 1 : 2; pass < 3; pass++) {
                    int[] spot = null; var lowest = -1L;
                    foreach (var x in xs) {
                        var costX = AxisCost(ax2, k, x, w);
                        if (spot != null && costX > lowest) continue;
                        var shut = blocked.Where(b => b[0] < x && x < b[1]).Select(b => new long[] { b[2], b[3] }).Concat(new[] { new[] { -BIG, 0L } });
                        if (pass < 2) shut = shut.Concat(wires.Select(line => LineRows(x, w, h, line)).Where(r => r != null));
                        if (pass < 1) shut = shut.Concat(ends.SelectMany((e, i) => avoid[i].Select(box => LinkRows(x, w, h, e, box))).Where(r => r != null));
                        var merged = new long[0][].ToList();
                        foreach (var r in shut.OrderBy(r => r[0]).ThenBy(r => r[1])) {
                            if (merged.Count > 0 && r[0] < merged[merged.Count - 1][1]) { if (r[1] > merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = r[1]; }
                            else merged.Add(new[] { r[0], r[1] });
                        }
                        var around = merged.FirstOrDefault(r => r[0] < best && best < r[1]);
                        foreach (var y in (around == null ? new long[] { best } : around).Where(v => v >= 0 && v < BIG / 2)) {
                            var cost = costX + AxisCost(ay2, k, (int)y, h);
                            if (spot == null || cost < lowest || cost == lowest && (y < spot[1] || y == spot[1] && x < spot[0])) { lowest = cost; spot = new[] { x, (int)y, pass }; }
                        }
                    }
                    if (spot != null) return spot;
                }
                return new[] { xs[xs.Length - 1], 0, 2 };
            }
            // Top-left corners at which a w by h note would come within gap of another note at (x, y, w, h).
            int[] NoteBlock(int w, int h, int[] other, int gap) {
                return new[] { other[0] - w - gap, other[0] + other[2] + gap, other[1] - h - gap, other[1] + other[3] + gap };
            }
            var paths = flows.Select(flow => flow.Path).ToList();
            var unmatched = only.Where(name => !paths.Any(path => path == name || path.EndsWith("/" + name))).ToList();
            if (unmatched.Count > 0) throw new ArgumentException("no logic flow matches " + string.Join(", ", unmatched) + ". Flows in the module: " + string.Join(", ", paths));

            var scanned = 0; var changing = 0; var moving = 0; var wrapping = 0;
            var lines = new string[0].ToList();

            for (var fi = 0; fi < flows.Count; fi++) {
                var flowPath = paths[fi];
                if (only.Length > 0 && !only.Any(name => flowPath == name || flowPath.EndsWith("/" + name))) continue;
                scanned++;
                var all = flows[fi].Nodes.ToList();
                var code = all.Where(n => n.Kind != NodeKind.Comment).ToList();
                var notes = all.Where(n => n.Kind == NodeKind.Comment).ToList();
                var N = code.Count;
                if (N == 0) continue;
                var order = code.Select((n, i) => i).ToList();
                var ix = code.Select((n, i) => new { n, i }).ToDictionary(p => p.n, p => p.i);
                var oldX = code.Select(n => n.X).ToArray();
                var oldY = code.Select(n => n.Y).ToArray();

                // 1. The graph: for each node, its targets in branch order
                //    (If: True, False; Switch: conditions, Otherwise; For Each: cycle, next).
                var outs = code.Select(n => n.Targets.Where(t => t != null && ix.ContainsKey(t)).Select(t => ix[t]).Distinct().ToList()).ToList();
                var isLoop = code.Select(n => n.Kind == NodeKind.ForEach).ToArray();
                var isRaise = code.Select(n => n.Kind == NodeKind.Raise).ToArray();
                var isSwitch = code.Select(n => n.Kind == NodeKind.Switch).ToArray();
                var isStart = code.Select(n => n.Kind == NodeKind.Start).ToArray();
                var body = code.Select(n => {
                    var cycle = n.Kind == NodeKind.ForEach && n.Targets.Count > 0 ? n.Targets[0] : null;
                    var next = n.Kind == NodeKind.ForEach && n.Targets.Count > 1 ? n.Targets[1] : null;
                    return cycle != null && next != null && ix.ContainsKey(cycle) ? ix[cycle] : -1;
                }).ToArray();

                // 2. Roots, in placement order: Start, exception handlers (left to right),
                //    then nodes nothing leads to. A depth-first walk from each root marks
                //    the loop-back connectors, which root owns each node and the order
                //    the walk reaches them in (it never depends on the model's order).
                var state = new int[N]; var owner = new int[N]; var rank = new int[N]; var ranks = 0;
                var back = new int[0].ToHashSet();
                void Visit(int u, int root) {
                    state[u] = 1; owner[u] = root; rank[u] = ranks++;
                    foreach (var v in outs[u]) { if (state[v] == 1) back.Add(u * N + v); else if (state[v] == 0) Visit(v, root); }
                    state[u] = 2;
                }
                var roots = order.Where(i => isStart[i])
                    .Concat(order.Where(i => code[i].Kind == NodeKind.ExceptionHandler).OrderBy(i => oldX[i]).ThenBy(i => oldY[i]).ThenBy(i => i)).ToList();
                foreach (var root0 in roots) if (state[root0] == 0) Visit(root0, root0);
                var strays = order.Where(i => state[i] == 0).OrderBy(i => oldX[i]).ThenBy(i => oldY[i]).ThenBy(i => i).ToList();
                var fed = new bool[N];
                foreach (var source in order) foreach (var target in outs[source]) fed[target] = true;
                foreach (var stray in strays.Where(i => !fed[i]).Concat(strays)) if (state[stray] == 0) { roots.Add(stray); Visit(stray, stray); }
                var preds = order.Select(i => new int[0].ToList()).ToList();
                foreach (var source in order) foreach (var target in outs[source]) if (!back.Contains(source * N + target) && owner[source] == owner[target]) preds[target].Add(source);

                // 3. Placement on a grid of columns and rows.
                var col = new int[N]; var row = new int[N]; var placed = new bool[N];
                var ringed = new bool[N];                            // placed as part of a For Each loop's block
                var half = new bool[N];                              // a branch of a split If (3b): drawn half a column left of its column
                var free = new int[(N + 2) * (ROOT_GAP + 2)];        // per column, the first free half row: a row below its lowest node
                var top = -ROOT_GAP;                                 // rightmost column used so far
                var reach = 0;                                       // rightmost column used by the branch being placed
                var drawn = new int[0].Select(q => new int[4]).ToList();   // connectors with both ends placed: column, row, column, row
                var blockEdges = new int[0].Select(q => new int[2]).ToList();   // connectors of the loop Block() laid out last
                bool Reaches(int u, int goal, bool[] seen) {
                    if (u == goal) return true;
                    if (seen[u]) return false;
                    seen[u] = true;
                    return outs[u].Any(v => !back.Contains(u * N + v) && Reaches(v, goal, seen));
                }
                void Collect(int u, bool[] seen) {
                    if (seen[u]) return;
                    seen[u] = true;
                    foreach (var v in outs[u]) if (!back.Contains(u * N + v)) Collect(v, seen);
                }
                // How many nodes only the branch from u to v leads to (not reached from u's other branches).
                int Own(int u, int v) {
                    var mine = new bool[N]; Collect(v, mine);
                    var others = new bool[N]; foreach (var w in outs[u]) if (w != v) Collect(w, others);
                    return order.Count(i => mine[i] && !others[i]);
                }
                // How many nodes the branch from u to v leads to that u's other branches also lead to.
                int Shared(int u, int v) {
                    var mine = new bool[N]; Collect(v, mine);
                    var others = new bool[N]; foreach (var w in outs[u]) if (w != v) Collect(w, others);
                    return order.Count(i => mine[i] && others[i]);
                }

                // 3a. The shape of a loop: a For Each, or a node a connector loops back
                //     to (a loop built with an If). The nodes only its cycle leads to
                //     are laid out together, as cells relative to that node, the head:
                //     the arrangement with the lowest energy. A short cycle closes into a
                //     ring of short connectors; a long one keeps flowing down, with one
                //     long connector back. Found by placing the nodes one at a time next to their
                //     neighbours and keeping the BEAM best partial arrangements.
                //     Returns { node, columns right of the head, half rows below it }.
                //     The loop is left from its pivot, to the node after it: a For Each
                //     is its own pivot. Any other loop is left from the node of its cycle
                //     whose connector out of it leads to the most nodes (not to a Raise
                //     Exception; nearest the head on a tie). When that isn't the head,
                //     the pivot goes in the head's column below it, with the nodes that
                //     lead only from one to the other between them, so that the flow
                //     goes on down that column after the loop; every other node of the
                //     loop goes to the right of it.
                var pivot = -1; var after = -1;                      // set by Block(): the loop's pivot, and the node after the loop
                var centred = 0;                                     // set by Block(): half rows its block moved to centre on the head
                int[][] Block(int F) {
                    blockEdges.Clear(); pivot = F; after = -1; centred = 0;
                    // The cycle of a loop that isn't a For Each: every node that leads to a
                    // connector back to the head without passing through the head,
                    // through the cycles of the loops inside it too.
                    var reached = new bool[N]; foreach (var v in outs[F]) if (!back.Contains(F * N + v)) Collect(v, reached);
                    var circuit = new int[0].ToList();
                    var tracing = isLoop[F] ? new int[0].ToList() : order.Where(s => !placed[s] && back.Contains(s * N + F)).ToList();
                    while (tracing.Count > 0) {
                        var x = tracing[tracing.Count - 1]; tracing.RemoveAt(tracing.Count - 1);
                        if (x == F || placed[x] || !reached[x] || circuit.Contains(x)) continue;
                        circuit.Add(x); tracing.AddRange(order.Where(p => outs[p].Contains(x)));
                    }
                    circuit = circuit.OrderBy(x => x).ToList();
                    var stack = isLoop[F] ? outs[F].Where(v => v != body[F] && !back.Contains(F * N + v)).ToList() : new int[0].ToList();
                    if (!isLoop[F]) {
                        var hops = order.Select(i => N).ToArray(); hops[F] = 0;
                        var queue = new[] { F }.ToList();
                        for (var head = 0; head < queue.Count; head++) foreach (var v in outs[queue[head]]) if (circuit.Contains(v) && hops[v] == N) { hops[v] = hops[queue[head]] + 1; queue.Add(v); }
                        int Count(int t) { var seen = new bool[N]; Collect(t, seen); return order.Count(i => seen[i] && !placed[i]); }
                        var exit = new[] { F }.Concat(circuit).SelectMany(a => outs[a].Where(t => t != F && !circuit.Contains(t) && !placed[t] && !back.Contains(a * N + t)).Select(t => new[] { a, t }))
                            .OrderBy(e => isRaise[e[1]] ? 1 : 0).ThenBy(e => -Count(e[1])).ThenBy(e => hops[e[0]]).ThenBy(e => rank[e[0]]).ThenBy(e => outs[e[0]].IndexOf(e[1])).FirstOrDefault();
                        if (exit != null) { pivot = exit[0]; after = exit[1]; stack.Add(exit[1]); }
                    }
                    var inside = new int[0].ToList();
                    var start = isLoop[F] ? new[] { body[F] }.ToList() : outs[F].Where(v => !back.Contains(F * N + v)).ToList();
                    while (start.Count > 0) {
                        var x = start[start.Count - 1]; start.RemoveAt(start.Count - 1);
                        if (x == F || placed[x] || inside.Contains(x)) continue;
                        inside.Add(x); start.AddRange(outs[x].Where(v => !back.Contains(x * N + v)));
                    }
                    var beyond = new int[0].ToList();
                    while (stack.Count > 0) {
                        var x = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                        if (beyond.Contains(x)) continue;
                        beyond.Add(x); stack.AddRange(outs[x].Where(v => !back.Contains(x * N + v)));
                    }
                    inside = inside.Where(x => !beyond.Contains(x)).ToList();
                    for (var again = true; again; ) {
                        again = false;
                        foreach (var x in inside.ToList()) if (preds[x].Any(p => p != F && !inside.Contains(p))) { inside.Remove(x); again = true; }
                    }
                    if (isLoop[F] ? !inside.Contains(body[F]) : circuit.Count == 0 || !circuit.All(x => inside.Contains(x))) return new int[0][];
                    var members = new[] { F }.Concat(inside.OrderBy(x => x)).ToList();
                    var edges = members.SelectMany(a => outs[a].Where(b => b != a && members.Contains(b)).Select(b => new[] { a, b })).ToList();
                    var loops = new[] { F }.ToList();                // the nodes that lead back to the head
                    for (var grew = true; grew; ) {
                        grew = false;
                        foreach (var e in edges) if (loops.Contains(e[1]) && !loops.Contains(e[0])) { loops.Add(e[0]); grew = true; }
                    }
                    // The nodes that may go in the head's column: from the head to the pivot.
                    var column = new int[0].ToList();
                    if (pivot != F) {
                        for (var x = F; ; ) {
                            var nexts = outs[x].Where(v => members.Contains(v) && !back.Contains(x * N + v)).ToList();
                            if (nexts.Count != 1 || nexts[0] == pivot || preds[nexts[0]].Count != 1) break;
                            x = nexts[0]; column.Add(x);
                        }
                        column.Add(pivot);
                    }
                    var seq = new[] { F }.ToList();                  // placement order: depth first, end nodes before the rest
                    var via = new[] { F }.ToList();                  // the node each one was reached from
                    void Walk(int u) { foreach (var v in outs[u].OrderBy(t => outs[t].Count == 0 ? 0 : 1)) if (!seq.Contains(v) && members.Contains(v)) { seq.Add(v); via.Add(u); Walk(v); } }
                    Walk(F);
                    foreach (var x in members) if (!seq.Contains(x)) { seq.Add(x); via.Add(F); }
                    var m = seq.Count;
                    // The body of a For Each inside the loop (the nodes only its cycle leads
                    // to) stays to the right of that For Each, as a loop's block does.
                    var within = seq.Select(x => new int[0].ToList()).ToList();
                    foreach (var L in members) if (L != F && isLoop[L] && body[L] >= 0 && members.Contains(body[L])) {
                        var fromCycle = new bool[N]; fromCycle[L] = true; Collect(body[L], fromCycle);
                        var fromNext = new bool[N]; foreach (var v in outs[L]) if (v != body[L] && !back.Contains(L * N + v)) Collect(v, fromNext);
                        foreach (var x in members) if (x != L && fromCycle[x] && !fromNext[x]) within[seq.IndexOf(x)].Add(seq.IndexOf(L));
                    }
                    var E = edges.Select(e => new[] { seq.IndexOf(e[0]), seq.IndexOf(e[1]) }).ToList();
                    var cyc = edges.Select(e => loops.Contains(e[0]) && loops.Contains(e[1])).ToList();
                    // An end node of an If or Switch, and every connector of a plain chain that ends
                    // separately from one (as outside a loop), runs to the right along the row.
                    var tail = new int[0].ToHashSet();
                    foreach (var a in members) if (outs[a].Count > 1) foreach (var v in outs[a]) {
                        var chain = Chain(a, v);
                        if (chain.Length < 2 || !chain.All(x => members.Contains(x))) continue;
                        tail.Add(a * N + chain[0]);
                        for (var q = 0; q + 1 < chain.Length; q++) tail.Add(chain[q] * N + chain[q + 1]);
                    }
                    var leaf = edges.Select(e => outs[e[1]].Count == 0 && outs[e[0]].Count > 1 || tail.Contains(e[0] * N + e[1])).ToList();
                    var isBack = edges.Select(e => back.Contains(e[0] * N + e[1])).ToList();
                    var fork = edges.Select(e => outs[e[0]].Count > 1 && !isLoop[e[0]] || outs[e[1]].Count > 1 && !isLoop[e[1]]).ToList();
                    var cycle = edges.Select(e => isLoop[e[0]] ? body[e[0]] == e[1] : e[0] == pivot && loops.Contains(e[1])).ToList();
                    var par = via.Select(x => seq.IndexOf(x)).ToList();
                    var nb = seq.Select(x => new int[0].ToList()).ToList();
                    foreach (var e in E) { if (!nb[e[0]].Contains(e[1])) nb[e[0]].Add(e[1]); if (!nb[e[1]].Contains(e[0])) nb[e[1]].Add(e[0]); }
                    // A state is { sort key, energy, x of each node, y of each node }. A search
                    // that finds no free cell for a node starts again keeping four times as
                    // many arrangements; only then is the loop left to the rules.
                    var states = new[] { new int[2 + 2 * m] }.ToList(); var width = BEAM;
                    for (var i = 1; i < m; i++) {
                        // bound[j]: connectors between node i and the placed node j, through nodes not placed yet
                        var dist = seq.Select(x => -1).ToArray(); var bound = seq.Select(x => -1).ToArray();
                        dist[i] = 0;
                        var queue = new[] { i }.ToList();
                        for (var head = 0; head < queue.Count; head++) foreach (var n in nb[queue[head]]) {
                            if (n < i) { if (bound[n] < 0) bound[n] = dist[queue[head]] + 1; }
                            else if (dist[n] < 0) { dist[n] = dist[queue[head]] + 1; queue.Add(n); }
                        }
                        var mine = E.Select((e, k) => k).Where(k => (E[k][0] == i && E[k][1] < i) || (E[k][1] == i && E[k][0] < i)).ToList();
                        var old = E.Select((e, k) => k).Where(k => E[k][0] < i && E[k][1] < i).ToList();
                        var next = new int[0].Select(q => new int[0]).ToList();
                        foreach (var s in states) {
                            // candidate cells: STEPS away from a placed neighbour, right of the head's column; a cell is x * 100000 + y + 50000.
                            // The pivot of a loop that isn't left from its head goes in the head's column, below the nodes already there,
                            // and so may the nodes between them.
                            var low = 0;
                            for (var j = 1; j < i; j++) if (s[2 + j] == 0 && s[2 + m + j] > low) low = s[2 + m + j];
                            var down = column.Contains(seq[i]);
                            var cands = nb[i].Where(n => n < i).SelectMany(n => STEPS.Select(d => (s[2 + n] + d[0]) * 100000 + s[2 + m + n] + d[1] + 50000))
                                .Where(c => (seq[i] != pivot && c / 100000 >= 1 || down && c >= 50000 + low + 1 && c < 100000) && within[i].All(j => j >= i || c / 100000 > s[2 + j])).Distinct().OrderBy(c => Abs(c % 100000 - 50000)).ThenBy(c => -(c % 100000)).ThenBy(c => c / 100000).ToList();
                            foreach (var cand in cands) {
                                var cx = cand / 100000; var cy = cand % 100000 - 50000;
                                var taken = false;
                                for (var j = 0; j < i; j++) if (s[2 + j] == cx && s[2 + m + j] == cy) taken = true;
                                if (taken) continue;
                                var t = s.ToArray(); t[2 + i] = cx; t[2 + m + i] = cy;
                                if (old.Any(k => Through(t[2 + E[k][0]], t[2 + m + E[k][0]], t[2 + E[k][1]], t[2 + m + E[k][1]], cx, cy))) continue;
                                var cost = 0; var ok = true;
                                foreach (var k in mine) {
                                    var a = E[k][0]; var b = E[k][1];
                                    var dx = t[2 + b] - t[2 + a]; var dy = t[2 + m + b] - t[2 + m + a];
                                    for (var j = 0; j < i; j++) if (Through(t[2 + a], t[2 + m + a], t[2 + b], t[2 + m + b], t[2 + j], t[2 + m + j])) ok = false;
                                    if (!ok) break;
                                    var cheb = Abs(dx) > Abs(dy) ? Abs(dx) : Abs(dy);
                                    if (!(leaf[k] && dx == 1 && dy == 0)) {
                                        cost += (leaf[k] ? LEAF : 0) + LATERAL * Abs(dx);
                                        if (cheb > 1) cost += (isBack[k] ? LONGBACK * (cheb - 1 < BACKCAP ? cheb - 1 : BACKCAP) : (dx == 0 || dy == 0 ? STRAIGHT : LONG) * (cheb - 1)) + (dx == 0 || dy == 0 || Abs(dx) == Abs(dy) ? 0 : OFFAXIS);
                                        if (dy < 0 && !isBack[k]) cost += (cyc[k] ? CLIMB : UP) * -dy;
                                        if (dy > 0 && cycle[k]) cost += DIVE;
                                        if (fork[k] && dx != 0 && dy != 0) cost += FORKDIAG;
                                    }
                                    if (isBack[k] && a == i) {
                                        // twice the area enclosed by the cycle this connector closes
                                        var area = 0; var node = i; var hops = 0;
                                        while (node != b && hops < m) { var prev = par[node]; area += t[2 + prev] * t[2 + m + node] - t[2 + node] * t[2 + m + prev]; node = prev; hops++; }
                                        if (node == b) { area = Abs(area + t[2 + i] * t[2 + m + b] - t[2 + b] * t[2 + m + i]); cost -= PRESSURE * (area < AREACAP ? area : AREACAP); }
                                    }
                                    foreach (var k2 in old.Concat(mine.Where(q => q < k))) {
                                        var a2 = E[k2][0]; var b2 = E[k2][1];
                                        if (Crosses(t[2 + a], t[2 + m + a], t[2 + b], t[2 + m + b], t[2 + a2], t[2 + m + a2], t[2 + b2], t[2 + m + b2])) cost += CROSS;
                                        // At a For Each inside the loop, only the path through it (in from
                                        // before it, on to the node after it) and its own cycle (back in
                                        // from its body, out along Cycle) bend: as for a loop on its own,
                                        // the way into it doesn't turn into its Cycle.
                                        var first = b2 == a ? k2 : k; var second = b2 == a ? k : k2;
                                        var joint = E[first][1];
                                        if (cyc[k] && cyc[k2] && (b2 == a || b == a2) && !(joint != 0 && isLoop[seq[joint]] && isBack[first] != cycle[second])) {
                                            var turn = Abs(Dir(t[2 + E[first][1]] - t[2 + E[first][0]], t[2 + m + E[first][1]] - t[2 + m + E[first][0]]) - Dir(t[2 + E[second][1]] - t[2 + E[second][0]], t[2 + m + E[second][1]] - t[2 + m + E[second][0]]));
                                            if (turn > 4) turn = 8 - turn;
                                            cost += BEND * turn * turn;
                                        }
                                    }
                                }
                                if (!ok) continue;
                                var hint = 0;
                                for (var j = 0; j < i; j++) if (bound[j] >= 0) {
                                    var gap = (Abs(t[2 + j] - cx) > Abs(t[2 + m + j] - cy) ? Abs(t[2 + j] - cx) : Abs(t[2 + m + j] - cy)) - bound[j];
                                    if (gap > 0) hint += isLoop[seq[j]] || j == 0 ? LONGBACK * (gap < BACKCAP ? gap : BACKCAP) : FAR * gap;
                                }
                                t[1] = s[1] + cost; t[0] = t[1] + hint;
                                next.Add(t);
                            }
                        }
                        if (next.Count == 0 && width > BEAM) return new int[0][];
                        if (next.Count == 0) { width = 4 * BEAM; states = new[] { new int[2 + 2 * m] }.ToList(); i = 0; continue; }
                        states = next.OrderBy(s => s[0]).Take(width).ToList();
                    }
                    var best = states.OrderBy(s => s[1]).First();
                    blockEdges.AddRange(edges);
                    // Half height: the block is drawn half as tall, its rows becoming half
                    // rows, when every node stays at least a row from the others in its
                    // column (the head's included) and no connector comes within half a
                    // cell of a node it wasn't that close to before. So a ring of
                    // For Each → A → B → C → back is a flat diamond, A half a row above
                    // the For Each's row, B on it and C half a row below.
                    var flat = true;
                    for (var i = 0; i < m; i++) for (var j = i + 1; j < m; j++) if (best[2 + i] == best[2 + j] && Abs(best[2 + m + i] - best[2 + m + j]) < 2) flat = false;
                    foreach (var e in E) for (var j = 0; j < m && flat; j++) {
                        int ax = best[2 + e[0]], ay = best[2 + m + e[0]], bx = best[2 + e[1]], by = best[2 + m + e[1]];
                        if (j != e[0] && j != e[1] && Near(ax, ay, bx, by, best[2 + j], best[2 + m + j]) && !Near(ax, 2 * ay, bx, 2 * by, best[2 + j], 2 * best[2 + m + j])) flat = false;
                    }
                    var dys = seq.Select((x, i) => (flat ? 1 : 2) * best[2 + m + i]).ToArray();
                    // Centred: when the For Each's connectors in the block (its Cycle and
                    // the ones back to it) reach a row further below its row than above it,
                    // the block moves half a row up so they mirror each other, unless that
                    // turns a straight connector of an If or Switch diagonal or brings a
                    // connector within half a cell of a node (and, in Place, unless it would
                    // then go lower). So For Each → A → B → back is a flat triangle, A half a
                    // row above the For Each's row and B half a row below. Never down: half
                    // a row lower at the bottom would push a node below it down a whole row.
                    var centre = 0;
                    if (isLoop[F] && m > 1) {
                        var ends = E.Where(e => e[0] == 0 || e[1] == 0).Select(e => dys[e[0] == 0 ? e[1] : e[0]]).ToList();
                        var lowest = ends.Min(); var highest = ends.Max();
                        var move = lowest + highest == 2 ? -1 : 0;
                        var keeps = move != 0;
                        for (var k = 0; k < E.Count && keeps; k++) {
                            if (E[k][0] != 0 && E[k][1] != 0) continue;
                            var other = E[k][0] == 0 ? E[k][1] : E[k][0];
                            if (fork[k] && dys[other] == 0) keeps = false;
                            for (var j = 1; j < m && keeps; j++) if (j != other
                                && Near(0, 0, best[2 + other], dys[other] + move, best[2 + j], dys[j] + move) && !Near(0, 0, best[2 + other], dys[other], best[2 + j], dys[j])) keeps = false;
                        }
                        if (keeps) centre = move;
                        centred = centre;
                    }
                    return seq.Skip(1).Select((x, i) => new[] { x, best[3 + i], dys[i + 1] + centre }).ToArray();
                }

                // 3b. Everything else is placed by rule, one node at a time.
                // The nodes of a plain chain from v, a branch of u, that ends separately:
                // each leads to at most one node, nothing else leads into it, none is a
                // For Each, and the last leads nowhere (End, Raise Exception). Empty when
                // the branch from v isn't one.
                int[] Chain(int u, int v) {
                    var chain = new int[0].ToList();
                    for (int p = u, x = v; ; p = x, x = outs[x][0]) {
                        if (placed[x] || chain.Contains(x) || isLoop[x] || preds[x].Count != 1 || preds[x][0] != p || outs[x].Count > 1 || outs[x].Any(y => back.Contains(x * N + y))) return new int[0];
                        chain.Add(x);
                        if (outs[x].Count == 0) return chain.ToArray();
                    }
                }
                // The nodes of a plain chain from v, a branch of u, that rejoins u's other
                // branches: each leads to exactly one node, nothing else leads into it,
                // none is a For Each, and the last leads to a node another branch of u
                // also leads to. Empty when the branch from v isn't one.
                int[] Rejoin(int u, int v) {
                    var others = new bool[N]; foreach (var w in outs[u]) if (w != v) Collect(w, others);
                    var chain = new int[0].ToList();
                    for (int p = u, x = v; ; p = x, x = outs[x][0]) {
                        if (others[x]) return chain.ToArray();
                        if (placed[x] || chain.Contains(x) || isLoop[x] || preds[x].Count != 1 || preds[x][0] != p || outs[x].Count != 1 || back.Contains(x * N + outs[x][0])) return new int[0];
                        chain.Add(x);
                    }
                }
                // True when a chain can run along row r from column c0: every cell is free
                // and no drawn connector runs through or right beside one.
                bool Fits(int[] chain, int c0, int r) {
                    return chain.Select((x, i) => c0 + i).All(k => free[k] <= r && !drawn.Any(s => Near(s[0], s[1], s[2], s[3], k, r)));
                }
                // across: u is a node of a chain running along its row, so its next node
                // goes one column to the right instead of one row down. level: u is a
                // branch of a Switch's fan, which stays on its row unless a connector
                // would run exactly through it.
                void Place(int u, int c, int wanted, bool across, bool level) {
                    // A node where branches join goes in the leftmost column of the nodes
                    // leading to it: below those in its own column, and level with the
                    // last node of a branch in the next column. An end where branches
                    // join (it leads nowhere: End, Raise Exception) whose branches come
                    // from three columns goes in the middle one instead, when that column
                    // is free down to there, a branch in it is its lowest node, and each
                    // branch on either side is level with it or can move down to be
                    // (below): under the middle branch, level with the other two.
                    // Movable: a node that can move down its column, as the last node of
                    // a branch into an end: it leads only there, isn't part of a loop's
                    // block, every connector into it comes straight down its column, and
                    // nothing is below it in its column.
                    bool Movable(int p) { return !isLoop[p] && !ringed[p] && !half[p] && outs[p].Count == 1 && preds[p].Count > 0 && preds[p].All(q => col[q] == col[p] && row[q] < row[p]) && free[col[p]] == row[p] + 2; }
                    if (preds[u].Count > 1) {
                        var jl = preds[u].Min(p => col[p]); var jh = preds[u].Max(p => col[p]);
                        int JoinRow(int k) { var q = preds[u].Max(p => Abs(col[p] - k) == 1 ? row[p] : row[p] + 2); return q + (q & 1); }
                        c = outs[u].Count == 0 && jh - jl == 2 && free[jl + 1] <= JoinRow(jl + 1) && preds[u].All(p => col[p] != jl + 1 || free[jl + 1] == row[p] + 2)
                            && preds[u].All(p => col[p] == jl + 1 || row[p] == JoinRow(jl + 1) || Movable(p)) ? jl + 1 : jl;
                        wanted = JoinRow(c);
                    }
                    // A For Each, or a node a connector loops back to, brings its loop along as one block.
                    var block = isLoop[u] ? (body[u] >= 0 && !placed[body[u]] ? Block(u) : new int[0][])
                        : order.Any(s => !placed[s] && back.Contains(s * N + u)) ? Block(u) : new int[0][];
                    var links = block.Length > 0 ? blockEdges.ToList() : blockEdges.Take(0).ToList();
                    var loopPivot = block.Length > 0 ? pivot : -1; var loopNext = block.Length > 0 ? after : -1;
                    var loopCentre = block.Length > 0 ? centred : 0;
                    var inBlock = new bool[N]; var dxOf = new int[N]; var dyOf = new int[N];
                    foreach (var b in block) { inBlock[b[0]] = true; dxOf[b[0]] = b[1]; dyOf[b[0]] = b[2]; }
                    // Targets this node can place now, and targets still waiting for another branch.
                    var ready = outs[u].Where(v => !placed[v] && !inBlock[v] && !back.Contains(u * N + v) && preds[v].All(p => p == u || placed[p] || inBlock[p])).ToList();
                    var waiting = outs[u].Where(v => !placed[v] && !inBlock[v] && !back.Contains(u * N + v) && !ready.Contains(v)).ToList();
                    // A branch is a detour when it rejoins a target still waiting for
                    // another branch and that join will stay in this column. A join that a
                    // node already placed further left also leads to goes in that column
                    // instead, so it doesn't stop the flow going down here.
                    var detour = waiting.Any(w => !preds[w].Any(p => placed[p] && col[p] < c) && ready.Any(v => Reaches(v, w, new bool[N])));
                    // The spine continues straight down; every other ready target is a side
                    // branch. After a For Each, or a loop this node is the pivot of, the
                    // node after the loop continues down. Otherwise, of the ready targets
                    // that aren't a Raise Exception, the one with the most nodes of its
                    // own continues down (so a short branch, such as an early exit, is the
                    // one to the right); on a tie, the last one (False). A branch that rejoins the
                    // other as a plain chain continues down instead (the shortest such;
                    // on a tie, the last): the other branch goes to the right, starting
                    // on this node's row, so it ends beside the join rather than far
                    // above it, and the join stays in this column below the chain. There
                    // is no spine when a branch is a detour.
                    // A Switch fans its branches out instead, side by side on the row below
                    // it. First the branches that rejoin (that lead to a node another
                    // branch also leads to), in branch order; when none does, the one
                    // with the most nodes of its own, where the flow goes on (on a tie,
                    // the first). Then the other branches, in branch order, and last the
                    // ones that end separately: a plain chain to an End or Raise
                    // Exception. The first goes straight down, or one column to the right
                    // when a detour's join stays in this column, and then on the Switch's
                    // own row when it is the only one. One branch that ends separately goes
                    // aside instead, to the right on the Switch's own row, as at an If,
                    // when another branch goes on: the shortest, on a tie the last, and
                    // only when it is no longer than the longest branch that goes on.
                    var spine = -1; var aside = -1; var fan = new int[0].ToList();
                    if (isLoop[u]) spine = ready.Where(v => v != body[u]).DefaultIfEmpty(-1).Last();
                    else if (loopPivot == u) spine = ready.Contains(loopNext) ? loopNext : -1;
                    else if (isSwitch[u]) {
                        var exits = ready.Where(v => Chain(u, v).Length > 0).ToList();
                        var going = ready.Where(v => !exits.Contains(v) && preds[v].Count == 1).ToList();
                        var longest = going.Select(v => Own(u, v)).DefaultIfEmpty(0).Max();
                        aside = exits.Where(v => Chain(u, v).Length <= longest).OrderBy(v => Chain(u, v).Length).ThenBy(v => -outs[u].IndexOf(v)).DefaultIfEmpty(-1).First();
                        var lead = going.Where(v => Shared(u, v) > 0).ToList();
                        if (lead.Count == 0) lead = going.OrderBy(v => -Own(u, v)).ThenBy(v => outs[u].IndexOf(v)).Take(1).ToList();
                        fan = lead.Concat(going.Where(v => !lead.Contains(v))).Concat(exits.Where(v => v != aside)).ToList();
                        if (!detour && fan.Count > 0) spine = fan[0];
                    }
                    else if (!detour) {
                        var pool = outs[u].Count < 2 || outs[u].All(v => isRaise[v]) ? ready : ready.Where(v => !isRaise[v]).ToList();
                        var rejoining = pool.Count > 1 ? pool.Where(v => Rejoin(u, v).Length > 0).ToList() : pool.Take(0).ToList();
                        spine = rejoining.Count > 0 ? rejoining.OrderBy(v => Rejoin(u, v).Length).ThenBy(v => -outs[u].IndexOf(v)).First()
                            : pool.OrderBy(v => outs[v].Count == 0 ? 0 : 1).ThenBy(v => Own(u, v)).ThenBy(v => outs[u].IndexOf(v)).DefaultIfEmpty(-1).Last();
                    }
                    var sides = ready.Where(v => v != spine).ToList();
                    // Row: below whatever is already in this column and, when this node
                    // has side branches, below everything already placed to its right.
                    // A single side branch that is a plain chain, ending separately or
                    // rejoining, only needs its own cells: the first row where the next
                    // column is free from there down (or, for a chain that runs along the
                    // row, where its cells on the row are free) and no drawn connector
                    // runs through or right beside a cell of the chain. So the node after
                    // a loop goes right below the loop's nodes in the next column, not
                    // below the whole loop. Only when no node placed to its right still
                    // leads to a node not placed yet: that connector, drawn later, would
                    // come down past the chain.
                    var pending = order.Any(p => placed[p] && col[p] > c && outs[p].Any(t => t != u && !placed[t] && !back.Contains(p * N + t)));
                    var lean = isSwitch[u] || sides.Count != 1 || pending ? new int[0] : Chain(u, sides[0]).Length > 0 ? Chain(u, sides[0]) : Rejoin(u, sides[0]);
                    bool Lean(int q) {
                        if (lean.Length > 1 && Chain(u, sides[0]).Length > 0 && Fits(lean, c + 1, q)) return true;
                        return free[c + 1] <= q && lean.Select((x, i) => q + 2 * i).All(y => !drawn.Any(s => Near(s[0], s[1], s[2], s[3], c + 1, y)));
                    }
                    // Rows on a whole row: half rows rounded up to an even one.
                    int Low() { var lo = wanted > free[c] ? wanted : free[c]; return lo + (lo & 1); }
                    int High() {
                        var hi = Low();
                        if (sides.Count > 0) for (var k = c + 1; k <= top; k++) if (free[k] > hi) hi = free[k];
                        return hi + (hi & 1);
                    }
                    int Row() {
                        for (var q = Low(); lean.Length > 0 && q < High(); q += 2) if (Lean(q)) return q;
                        return High();
                    }
                    var r = Row();
                    // A branch whose connector to this join would run exactly through a
                    // node moves down instead, a row at a time, while that keeps it above
                    // the join: when nothing placed leads on from it, it isn't part of a
                    // loop's block, its new cell is free and clear of drawn connectors,
                    // and the connectors into it stay clear of nodes.
                    if (preds[u].Count > 1)
                        for (var shifts = 0; shifts < OWN_PUSH; shifts++) {
                            var jr = r;
                            var w = order.Where(p => placed[p] && preds[u].Contains(p) && col[p] != c && !isLoop[p] && !ringed[p] && !half[p]
                                    && outs[p].All(t => t == u || !placed[t])
                                    && order.Any(o => placed[o] && o != p && Through(col[p], row[p], c, jr, col[o], row[o])))
                                .OrderBy(p => row[p]).ThenBy(p => col[p]).DefaultIfEmpty(-1).First();
                            if (w < 0) break;
                            var wc = col[w]; var nr = row[w] + 2;
                            if (nr >= r || order.Any(o => placed[o] && o != w && col[o] == wc && Abs(row[o] - nr) < 2)
                                || drawn.Any(seg => !(seg[0] == wc && seg[1] == row[w]) && !(seg[2] == wc && seg[3] == row[w]) && Near(seg[0], seg[1], seg[2], seg[3], wc, nr))
                                || preds[w].Any(p => placed[p] && order.Any(o => placed[o] && o != p && o != w && Through(col[p], row[p], wc, nr, col[o], row[o])))) break;
                            foreach (var seg in drawn) { if (seg[0] == wc && seg[1] == row[w]) seg[1] = nr; if (seg[2] == wc && seg[3] == row[w]) seg[3] = nr; }
                            row[w] = nr; if (nr + 2 > free[wc]) free[wc] = nr + 2;
                            r = Row();
                        }
                    // Then further down until the loop block (and a side chain tucked in
                    // as above) fits in free cells and no
                    // connector runs through a node: neither an already drawn connector
                    // through or right beside this node or its block, nor their own
                    // connectors through a node already placed (after three rows, only
                    // an exact hit still pushes this node down). A join keeps the
                    // standard spacing below its branches: only an exact hit pushes it,
                    // after its branches have moved down as far as they can. Its own
                    // connectors push a node at most OWN_PUSH rows, and never when they
                    // run straight along its column: moving down can't clear those. A
                    // branch of a Switch's fan, like a join, is pushed only by an exact hit:
                    // the fan's connectors pass close to each other's nodes by design.
                    var ends = order.Where(w => placed[w] && col[w] != c && (outs[w].Contains(u) || outs[u].Contains(w))).ToList();
                    int Descend(int from) {
                        var at = from;
                        for (var tries = 0; tries < 4 * N + 4; tries++) {
                            if ((at >= High() || Lean(at)) && block.All(b => at + b[2] >= 0 && at + b[2] >= free[c + b[1]])
                                && !drawn.Any(s => (level ? Through(s[0], s[1], s[2], s[3], c, at) : Near(s[0], s[1], s[2], s[3], c, at)) || block.Any(b => Near(s[0], s[1], s[2], s[3], c + b[1], at + b[2])))
                                && (tries >= OWN_PUSH || !ends.Any(w => order.Any(o => placed[o] && o != w && (preds[u].Count > 1 || level ? Through(col[w], row[w], c, at, col[o], row[o]) : tries < 3 ? Near(col[w], row[w], c, at, col[o], row[o]) : Through(col[w], row[w], c, at, col[o], row[o])))))
                                && !links.Any(e => order.Any(o => placed[o] && Through(c + dxOf[e[0]], at + dyOf[e[0]], c + dxOf[e[1]], at + dyOf[e[1]], col[o], row[o])))) break;
                            at += 2;
                        }
                        return at;
                    }
                    // A block centred on its head (3a) moves back unless its head goes on the
                    // same row either way (lower costs a row, higher can tuck the ring in
                    // across a connector), and while a node placed to the right still leads
                    // to a node not placed yet: that connector, drawn later, could run
                    // through a node moved half a row.
                    void Shift(int by) { foreach (var b in block) { b[2] += by; dyOf[b[0]] = b[2]; } }
                    var centredRow = Descend(r);
                    if (loopCentre != 0) {
                        Shift(-loopCentre);
                        var plainRow = Descend(r);
                        if (pending || plainRow != centredRow) centredRow = plainRow; else Shift(loopCentre);
                    }
                    r = centredRow;
                    foreach (var end in ends) drawn.Add(new[] { col[end], row[end], c, r });
                    col[u] = c; row[u] = r; placed[u] = true; free[c] = r + 2;
                    // The last node of a branch in a column beside an end where branches
                    // join, above it, moves down level with it, so its connector to the
                    // end runs straight along the row: when it is Movable (above) and its
                    // new cell and its longer connectors are clear of the drawn ones.
                    if (preds[u].Count > 1 && outs[u].Count == 0) foreach (var bp in preds[u].OrderBy(p => col[p]).ThenBy(p => row[p])) {
                        var pc = col[bp]; var pr = row[bp];
                        if (Abs(pc - c) != 1 || pr >= r || !Movable(bp)) continue;
                        var kept = drawn.Where(z => z[0] == pc && z[1] == pr || z[2] == pc && z[3] == pr).ToList();
                        var rest = drawn.Where(z => !kept.Contains(z)).ToList();
                        if (rest.Any(z => Near(z[0], z[1], z[2], z[3], pc, r))) continue;
                        if (preds[bp].Concat(new[] { u }).Any(q => rest.Any(z => Crosses(col[q], row[q], pc, r, z[0], z[1], z[2], z[3])))) continue;
                        foreach (var seg in kept) { if (seg[0] == pc && seg[1] == pr) seg[1] = r; if (seg[2] == pc && seg[3] == pr) seg[3] = r; }
                        row[bp] = r; if (r + 2 > free[pc]) free[pc] = r + 2;
                    }
                    if (c > top) top = c;
                    if (c > reach) reach = c;
                    foreach (var b in block) {
                        col[b[0]] = c + b[1]; row[b[0]] = r + b[2]; placed[b[0]] = true; ringed[b[0]] = true;
                        if (row[b[0]] + 2 > free[col[b[0]]]) free[col[b[0]]] = row[b[0]] + 2;
                        if (col[b[0]] > top) top = col[b[0]];
                        if (col[b[0]] > reach) reach = col[b[0]];
                    }
                    foreach (var e in links) drawn.Add(new[] { col[e[0]], row[e[0]], col[e[1]], row[e[1]] });
                    // Whatever the block's nodes lead to outside the block: first the node
                    // after the loop, below its pivot.
                    foreach (var b in block.OrderBy(b => b[0] == loopPivot ? 0 : 1)) foreach (var exit in outs[b[0]].OrderBy(t => t == loopNext ? 0 : 1)) if (!placed[exit] && !back.Contains(b[0] * N + exit) && preds[exit].All(p => placed[p])) Place(exit, col[b[0]], row[b[0]] + 2, false, false);
                    // A Switch: the branch aside first, then the fan, left to right, each
                    // branch in the first column after the one before it where the fan's
                    // row is free (a chain aside running along the Switch's row leaves it
                    // free). A ready join goes after them, by its own rule.
                    if (isSwitch[u]) {
                        var keep = reach; var widest = c;
                        if (aside >= 0 && !placed[aside]) {
                            var chain = Chain(u, aside);
                            reach = c + 1;
                            Place(aside, c + 1, r, chain.Length > 1 && Fits(chain, c + 1, r), false);
                            if (reach > widest) widest = reach;
                        }
                        var fanAt = detour ? c + 1 : c;
                        var fanRow = detour && fan.Count == 1 && aside < 0 ? r : r + 2;
                        foreach (var v in fan) {
                            if (placed[v]) continue;
                            while (free[fanAt] > fanRow) fanAt++;
                            reach = fanAt;
                            Place(v, fanAt, fanRow, false, true);
                            if (reach > widest) widest = reach;
                            fanAt = reach + 1;
                        }
                        reach = keep > widest ? keep : widest;
                        foreach (var v in ready) if (!placed[v]) Place(v, c, r + 2, false, false);
                        return;
                    }
                    // A split: an If whose two branches are plain chains of the same length
                    // that rejoin at a node nothing else leads to opens into a diamond. Both
                    // branches start a row below the If, half a column either side of it
                    // (False left, True right), and meet again below it. They are placed as
                    // if False went straight down and True one column to the right, then
                    // drawn half a column left; the column left of this one is kept clear
                    // beside them. Only when that column and the next one are free from the
                    // row below the If down, nothing placed in the column to the left or in
                    // a column to the right still leads to a node not placed yet (its
                    // connector, drawn later, would come down past a branch), and the node
                    // they meet at isn't a loop's head (its ring may reach up beside them).
                    var split = false;
                    if (!isSwitch[u] && !isLoop[u] && block.Length == 0 && !across && !level && !detour && !pending && outs[u].Count == 2 && sides.Count == 1 && spine >= 0) {
                        var right = Rejoin(u, sides[0]); var left = Rejoin(u, spine);
                        if (right.Length > 0 && right.Length == left.Length) {
                            var meet = outs[right[right.Length - 1]][0];
                            split = outs[left[left.Length - 1]][0] == meet && preds[meet].Count == 2 && !isLoop[meet] && !order.Any(q => back.Contains(q * N + meet)) && free[c + 1] <= r + 2
                                && (c == 0 || free[c - 1] <= r + 2 && !order.Any(p => placed[p] && col[p] == c - 1 && outs[p].Any(t => !placed[t] && !back.Contains(p * N + t))));
                            if (split) {
                                foreach (var x in right.Concat(left)) half[x] = true;
                                if (c > 0 && r + 2 * left.Length + 2 > free[c - 1]) free[c - 1] = r + 2 * left.Length + 2;
                            }
                        }
                    }
                    // Side branches, left to right, each starting on this node's row (a split's
                    // a row below it). The only side branch of a node runs along that row to
                    // the right when it is a plain chain that ends separately and its cells
                    // are free.
                    var next = c + 1;
                    foreach (var side in sides) {
                        if (placed[side]) continue;
                        var keep = reach; reach = next;
                        var chain = sides.Count == 1 ? Chain(u, side) : new int[0];
                        Place(side, next, split ? r + 2 : r, !split && chain.Length > 1 && Fits(chain, next, r), false);
                        next = reach + 1;
                        if (keep > reach) reach = keep;
                    }
                    if (spine >= 0 && !placed[spine]) Place(spine, across ? c + 1 : c, across ? r : r + 2, across, false);
                }
                foreach (var root0 in roots) if (!placed[root0]) { reach = 0; Place(root0, top + ROOT_GAP, 0, false, false); }
                foreach (var rest in order) if (!placed[rest]) { reach = 0; Place(rest, top + ROOT_GAP, 0, false, false); }

                var newX = order.Select(i => isStart[i] ? START_X : X0 + (int)FloorDiv(2L * PITCH_N * col[i] - (half[i] ? PITCH_N : 0) + PITCH_D, 2 * PITCH_D)).ToArray();
                var newY = order.Select(i => isStart[i] ? START_Y : (PITCH_N * (row[i] + 1) + PITCH_D) / (2 * PITCH_D)).ToArray();
                var moved = 0;
                foreach (var i in order) if (oldX[i] != newX[i] || oldY[i] != newY[i]) { moved++; code[i].X = newX[i]; code[i].Y = newY[i]; }

                // 4. Comment notes. First the text: unwrapped (single line breaks
                //    joined, paragraphs kept) and wrapped at LINE_MAX. Then the place.
                var wrapped = 0;
                foreach (var m in notes) {
                    var text = m.Text ?? "";
                    var neat = WrapText(text);
                    if (neat != text) { wrapped++; m.Text = neat; }
                }
                var noteW = notes.Select(m => NOTE_PAD_X + CHAR_W * (m.Text ?? "").Split((char)10).Max(l => l.TrimEnd((char)13).Length)).ToArray();
                var noteH = notes.Select(m => NOTE_PAD_Y + LINE_H * (m.Text ?? "").Split((char)10).Length).ToArray();
                var noteLinks = notes.Select(m => m.Targets.Where(t => t != null && ix.ContainsKey(t)).Select(t => ix[t]).Distinct().OrderBy(n => n).ToArray()).ToArray();
                var noteIsLabel = notes.Select(m => { var t = m.Text ?? ""; return t.Split((char)10).Length == 1 && t.Length <= LABEL_MAX; }).ToArray();
                var noteX0 = notes.Select(m => m.X).ToArray(); var noteY0 = notes.Select(m => m.Y).ToArray();
                var noteX = noteX0.ToArray(); var noteY = noteY0.ToArray();
                var midX = order.Select(n => newX[n] + ICON / 2).ToArray(); var midY = order.Select(n => newY[n] + ICON / 2).ToArray();
                int[][] NodeBlocks(int w, int h) {
                    return order.Select(n => new[] { midX[n] - NODE_L - MARGIN - w, midX[n] + NODE_R + MARGIN, midY[n] - NODE_T - MARGIN - h, midY[n] + NODE_B + MARGIN }).ToArray();
                }
                // What a note's link keeps clear of, doubled: each node's icon, and its
                // label above it, CLEAR around both.
                var nodeBoxes = order.Select(n => new[] {
                    new[] { 2L * (midX[n] - ICON / 2 - CLEAR), 2L * (midY[n] - ICON / 2 - CLEAR), 2L * (midX[n] + ICON / 2 + CLEAR), 2L * (midY[n] + ICON / 2 + CLEAR) },
                    new[] { 2L * (midX[n] - NODE_L - CLEAR), 2L * (midY[n] - NODE_T - CLEAR), 2L * (midX[n] + NODE_R + CLEAR), 2L * (midY[n] - ICON / 2 + CLEAR) } }).ToArray();
                // A note's links: the icon centres that pull it (doubled), and for each
                // link the boxes its line must miss: every other node's.
                int[] PullX(int j) { return noteLinks[j].Select(n => 2 * midX[n]).ToArray(); }
                int[] PullY(int j) { return noteLinks[j].Select(n => 2 * midY[n]).ToArray(); }
                long[][] Ends(int j) { return noteLinks[j].Select(n => new[] { 2L * midX[n], 2L * midY[n] }).ToArray(); }
                long[][][] Avoid(int j) { return noteLinks[j].Select(n => order.Where(o => o != n).SelectMany(o => nodeBoxes[o]).ToArray()).ToArray(); }
                // Every connector, doubled, from icon centre to icon centre.
                var connectors = order.SelectMany(n => outs[n].Select(t => new[] { 2L * midX[n], 2L * midY[n], 2L * midX[t], 2L * midY[t] })).ToArray();
                // True when a note at (x, y) keeps to what Settle's tier asks: up to tier 1,
                // it lies on no connector; at tier 0, its links also keep clear of the other nodes.
                bool Clear(int j, int x, int y, int tier) {
                    long px = 2L * x + noteW[j], py = 2L * y + noteH[j];
                    var area = new[] { 2L * (x - CLEAR), 2L * (y - CLEAR), 2L * (x + noteW[j] + CLEAR), 2L * (y + noteH[j] + CLEAR) };
                    return (tier > 1 || connectors.All(line => Misses(line[0], line[1], line[2], line[3], area)))
                        && (tier > 0 || noteLinks[j].All(n => order.All(o => o == n || nodeBoxes[o].All(box => Misses(px, py, 2L * midX[n], 2L * midY[n], box)))));
                }

                // 4a. Labels: a linked label is centred on the nodes it links to, as their
                //     pull balances it, in the left margin of the highest one's flow (the
                //     main flow's, or an exception handler's own, so its connector never
                //     runs across another flow); an unlinked one starts on the row of the
                //     nearest node at or above where it was, in the main flow's margin.
                //     Each takes the first free slot from there downwards.
                var labelInfo = notes.Select((m, j) => {
                    var anchor = noteLinks[j].OrderBy(n => newY[n]).ThenBy(n => newX[n]).ThenBy(n => n).DefaultIfEmpty(-1).First();
                    var baseY = anchor >= 0 ? AxisBest(PullY(j), 1, noteH[j])
                        : order.Where(n => oldY[n] <= m.Y).OrderByDescending(n => oldY[n]).ThenBy(n => n).Select(n => newY[n]).DefaultIfEmpty(START_Y).First();
                    var flowCol = anchor >= 0 ? col[owner[anchor]] : 0;
                    // Half a column further left when a split's branch is drawn left of that column.
                    var flowRoot = anchor >= 0 ? owner[anchor] : roots.Count > 0 ? roots[0] : -1;
                    var aside = order.Any(n => half[n] && col[n] == flowCol && owner[n] == flowRoot) ? PITCH_N : 0;
                    return new { j, baseY, isLinked = anchor >= 0, labelX = X0 + (int)FloorDiv(2L * PITCH_N * flowCol - aside + PITCH_D, 2 * PITCH_D) - LABEL_GAP };
                }).Where(q => noteIsLabel[q.j]).ToList();
                var stacked = new int[0].Select(q => new int[3]).ToList();
                foreach (var label in labelInfo.OrderBy(q => q.isLinked ? 0 : 1).ThenBy(q => q.isLinked ? q.baseY : noteY0[q.j]).ThenBy(q => noteY0[q.j]).ThenBy(q => noteX0[q.j]).ThenBy(q => q.j)) {
                    var lx = label.labelX; var ly = label.baseY; var lh = noteH[label.j];
                    while (stacked.Any(o => o[0] == lx && o[1] < ly + lh && ly < o[1] + o[2])) ly = stacked.Where(o => o[0] == lx && o[1] < ly + lh && ly < o[1] + o[2]).Max(o => o[1] + o[2]);
                    stacked.Add(new[] { lx, ly, lh });
                    noteX[label.j] = lx; noteY[label.j] = ly;
                }

                // 4b. Notes linked to no node (to nothing, or only to other notes) stay
                //     where they are, unless a node is within their margin: then they move
                //     the shortest way out. They don't move again after that.
                var settled = labelInfo.Select(q => new[] { noteX[q.j], noteY[q.j], noteW[q.j], noteH[q.j] }).ToList();
                foreach (var j in notes.Select((m, j) => j).Where(j => !noteIsLabel[j] && noteLinks[j].Length == 0).OrderBy(j => noteY0[j]).ThenBy(j => noteX0[j]).ThenBy(j => notes[j].Text ?? "").ThenBy(j => j)) {
                    var blocked = NodeBlocks(noteW[j], noteH[j]);
                    if (!Free(noteX[j], noteY[j], blocked)) {
                        var spot = Settle(noteW[j], noteH[j], new[] { 2 * noteX[j] + noteW[j] }, new[] { 2 * noteY[j] + noteH[j] }, 0, blocked, new long[0][], new long[0][][], new long[0][]);
                        noteX[j] = spot[0]; noteY[j] = spot[1];
                    }
                    settled.Add(new[] { noteX[j], noteY[j], noteW[j], noteH[j] });
                }

                // 4c. Linked notes, top to bottom (by their nodes, then by their text, which
                //     a second run sees unchanged, so it takes them in the same order; never
                //     by the order the model lists them in). Each has a rest point: where the pull of
                //     its links is least, outside every node's margin, clear of the notes
                //     settled above, and with every link clear of the other nodes.
                //     Starting from there, each step every note moves part of the way back
                //     to its rest point, then contacts are resolved pair by pair: two notes
                //     too close push each other apart along the axis where they overlap
                //     least, half each, and a note pushed into a node's margin, or to where
                //     a link of it would cross a node, moves the shortest way out. The
                //     simulation stops when a step moves nothing.
                var bodies = notes.Select((m, j) => j).Where(j => !noteIsLabel[j] && noteLinks[j].Length > 0)
                    .OrderBy(j => noteLinks[j].Min(n => newY[n])).ThenBy(j => noteLinks[j].Min(n => newX[n])).ThenBy(j => notes[j].Text ?? "", StringComparer.Ordinal).ThenBy(j => noteY0[j]).ThenBy(j => noteX0[j]).ThenBy(j => j).ToList();
                var bodyBlocks = bodies.Select(j => NodeBlocks(noteW[j], noteH[j]).Concat(settled.Select(o => NoteBlock(noteW[j], noteH[j], o, NOTE_SPACE))).ToArray()).ToList();
                var linkEnds = bodies.Select(j => Ends(j)).ToList(); var linkAvoid = bodies.Select(j => Avoid(j)).ToList();
                var restX = new int[bodies.Count]; var restY = new int[bodies.Count]; var tiers = new int[bodies.Count];
                for (var bi = 0; bi < bodies.Count; bi++) {
                    var j = bodies[bi];
                    var rest = Settle(noteW[j], noteH[j], PullX(j), PullY(j), 1, bodyBlocks[bi], linkEnds[bi], linkAvoid[bi], connectors);
                    restX[bi] = rest[0]; restY[bi] = rest[1]; tiers[bi] = rest[2]; noteX[j] = rest[0]; noteY[j] = rest[1];
                }
                for (var simStep = 0; simStep < SIM_STEPS; simStep++) {
                    var before = bodies.Select(j => noteX[j] * 1L * (1 << 21) + noteY[j]).ToArray();
                    for (var bi = 0; bi < bodies.Count; bi++) { var j = bodies[bi]; noteX[j] += (restX[bi] - noteX[j]) / PULL; noteY[j] += (restY[bi] - noteY[j]) / PULL; }
                    for (var pass = 0; pass < CONTACT_PASSES; pass++) {
                        var touching = false;
                        for (var ba = 0; ba < bodies.Count; ba++)
                            for (var bb = ba + 1; bb < bodies.Count; bb++) {
                                var ja = bodies[ba]; var jb = bodies[bb];
                                var ox = Min(noteX[ja] + noteW[ja] + NOTE_SPACE - noteX[jb], noteX[jb] + noteW[jb] + NOTE_SPACE - noteX[ja]);
                                var oy = Min(noteY[ja] + noteH[ja] + NOTE_SPACE - noteY[jb], noteY[jb] + noteH[jb] + NOTE_SPACE - noteY[ja]);
                                if (ox <= 0 || oy <= 0) continue;
                                touching = true;
                                if (ox < oy) {
                                    var away = 2 * noteX[ja] + noteW[ja] <= 2 * noteX[jb] + noteW[jb] ? -1 : 1;
                                    noteX[ja] += away * (ox / 2); noteX[jb] -= away * (ox - ox / 2);
                                } else {
                                    var away = 2 * noteY[ja] + noteH[ja] <= 2 * noteY[jb] + noteH[jb] ? -1 : 1;
                                    noteY[ja] += away * (oy / 2); noteY[jb] -= away * (oy - oy / 2);
                                }
                            }
                        for (var bi = 0; bi < bodies.Count; bi++) {
                            var j = bodies[bi];
                            if (Free(noteX[j], noteY[j], bodyBlocks[bi]) && Clear(j, noteX[j], noteY[j], tiers[bi])) continue;
                            touching = true;
                            var spot = Settle(noteW[j], noteH[j], new[] { 2 * noteX[j] + noteW[j] }, new[] { 2 * noteY[j] + noteH[j] }, 0, bodyBlocks[bi], tiers[bi] < 1 ? linkEnds[bi] : new long[0][], linkAvoid[bi], tiers[bi] < 2 ? connectors : new long[0][]);
                            noteX[j] = spot[0]; noteY[j] = spot[1];
                        }
                        if (!touching) break;
                    }
                    if (bodies.Select(j => noteX[j] * 1L * (1 << 21) + noteY[j]).SequenceEqual(before)) break;
                }
                // A note the simulation left overlapping one above it (closer than half
                // the space between notes) takes the best free place left.
                for (var bi = 0; bi < bodies.Count; bi++) {
                    var j = bodies[bi];
                    var others = settled.Select(o => NoteBlock(noteW[j], noteH[j], o, NOTE_SPACE / 2)).ToArray();
                    if (!Free(noteX[j], noteY[j], others)) {
                        var spot = Settle(noteW[j], noteH[j], PullX(j), PullY(j), 1, bodyBlocks[bi].Concat(others).ToArray(), linkEnds[bi], linkAvoid[bi], connectors);
                        noteX[j] = spot[0]; noteY[j] = spot[1];
                    }
                    settled.Add(new[] { noteX[j], noteY[j], noteW[j], noteH[j] });
                }
                for (var j = 0; j < notes.Count; j++)
                    if (noteX[j] != noteX0[j] || noteY[j] != noteY0[j]) { moved++; notes[j].X = noteX[j]; notes[j].Y = noteY[j]; }

                if (moved > 0 || wrapped > 0) { changing++; moving += moved; wrapping += wrapped; lines.Add(flowPath + ": " + moved + " of " + all.Count + (wrapped > 0 ? ", " + wrapped + " wrapped" : "")); }
            }

            var report = new LayoutReport { Scanned = scanned, Changing = changing, Moving = moving, Wrapping = wrapping };
            report.Lines.AddRange(lines);
            return report;
        }
    }
}
