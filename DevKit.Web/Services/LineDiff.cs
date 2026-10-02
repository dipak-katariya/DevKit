namespace DevKit.Web.Services;

/// <summary>A span that differs between two versions: <c>old[OldStart..OldEnd)</c> became <c>new[NewStart..NewEnd)</c>.</summary>
public readonly record struct DiffHunk(int OldStart, int OldEnd, int NewStart, int NewEnd)
{
    public int OldLength => OldEnd - OldStart;
    public int NewLength => NewEnd - NewStart;
}

/// <summary>The hunks turning one line sequence into another.</summary>
/// <param name="Approximate">The cost budget ran out, so some hunks are coarser than the minimal diff.</param>
public sealed record DiffResult(IReadOnlyList<DiffHunk> Hunks, bool Approximate);

/// <summary>
/// Line diff over interned line ids: Myers' O(ND) algorithm with the linear-space middle-snake split,
/// so memory grows with the file rather than with its square. A cost budget bounds the time spent on
/// two versions with almost nothing in common; past it, the region still undecided is reported as
/// replaced wholesale. That is a valid diff, only a coarser one — never a wrong one.
/// </summary>
public static class LineDiff
{
    /// <summary>Well under a second of work, even on a large file that was heavily rewritten.</summary>
    public const long DefaultBudget = 50_000_000;

    public static DiffResult Compute(int[] oldIds, int[] newIds, long budget = DefaultBudget)
    {
        ArgumentNullException.ThrowIfNull(oldIds);
        ArgumentNullException.ThrowIfNull(newIds);

        var differ = new Differ(oldIds, newIds, budget);
        differ.Diff(new Region(0, oldIds.Length, 0, newIds.Length));
        return new DiffResult(ToHunks(differ.Runs, oldIds.Length, newIds.Length), differ.Exhausted);
    }

    private static List<DiffHunk> ToHunks(List<Run> runs, int oldLength, int newLength)
    {
        var hunks = new List<DiffHunk>();
        int oldAt = 0, newAt = 0;
        foreach (var run in runs)
        {
            if (run.Old > oldAt || run.New > newAt)
            {
                hunks.Add(new DiffHunk(oldAt, run.Old, newAt, run.New));
            }
            oldAt = run.Old + run.Length;
            newAt = run.New + run.Length;
        }
        if (oldAt < oldLength || newAt < newLength)
        {
            hunks.Add(new DiffHunk(oldAt, oldLength, newAt, newLength));
        }
        return hunks;
    }

    /// <summary>Equal lines: <c>old[Old..Old+Length)</c> matches <c>new[New..New+Length)</c>.</summary>
    private readonly record struct Run(int Old, int New, int Length);

    private readonly record struct Region(int ALo, int AHi, int BLo, int BHi)
    {
        public int ALength => AHi - ALo;
        public int BLength => BHi - BLo;
    }

    private sealed class Differ
    {
        private readonly int[] _a;
        private readonly int[] _b;
        private long _remaining;

        public Differ(int[] a, int[] b, long budget)
        {
            _a = a;
            _b = b;
            _remaining = budget;
        }

        public List<Run> Runs { get; } = new();
        public bool Exhausted { get; private set; }

        /// <summary>Emits the equal runs of <paramref name="r"/> in order: prefix, both halves, suffix.</summary>
        public void Diff(Region r)
        {
            var prefix = 0;
            while (prefix < r.ALength && prefix < r.BLength && _a[r.ALo + prefix] == _b[r.BLo + prefix])
            {
                prefix++;
            }
            if (prefix > 0)
            {
                Runs.Add(new Run(r.ALo, r.BLo, prefix));
            }

            var rest = r with { ALo = r.ALo + prefix, BLo = r.BLo + prefix };
            var suffix = 0;
            while (suffix < rest.ALength && suffix < rest.BLength
                   && _a[rest.AHi - 1 - suffix] == _b[rest.BHi - 1 - suffix])
            {
                suffix++;
            }

            var inner = rest with { AHi = rest.AHi - suffix, BHi = rest.BHi - suffix };
            if (inner.ALength > 0 && inner.BLength > 0 && TryBisect(inner, out var x, out var y))
            {
                Diff(inner with { AHi = x, BHi = y });
                Diff(inner with { ALo = x, BLo = y });
            }

            if (suffix > 0)
            {
                Runs.Add(new Run(inner.AHi, inner.BHi, suffix));
            }
        }

        private bool TryBisect(Region r, out int splitA, out int splitB)
        {
            splitA = splitB = 0;
            if (Exhausted)
            {
                return false;
            }

            var snake = new MiddleSnake(_a, _b, r);
            for (var d = 0; d < snake.MaxD; d++)
            {
                var found = snake.Forward(d, out splitA, out splitB) || snake.Reverse(d, out splitA, out splitB);
                _remaining -= snake.TakeSteps();
                if (found)
                {
                    // A split at either corner would recurse on the same region forever. It cannot
                    // happen once the common ends are trimmed, but the guard costs nothing.
                    var proper = (splitA, splitB) != (r.ALo, r.BLo) && (splitA, splitB) != (r.AHi, r.BHi);
                    return proper;
                }
                if (_remaining < 0)
                {
                    Exhausted = true;
                    return false;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// One middle-snake search: forward and reverse furthest-reaching paths advanced one edit at a
    /// time until they overlap. The overlap is a point on an optimal path, so splitting there and
    /// diffing both halves gives the minimal diff in linear space. Follows diff-match-patch's bisect.
    /// </summary>
    private sealed class MiddleSnake
    {
        private readonly int[] _a;
        private readonly int[] _b;
        private readonly Region _r;
        private readonly int _n;
        private readonly int _m;
        private readonly int _delta;
        private readonly int _offset;
        private readonly bool _front;
        private readonly int[] _v1;
        private readonly int[] _v2;
        private int _k1Start, _k1End, _k2Start, _k2End;
        private long _steps;

        public MiddleSnake(int[] a, int[] b, Region r)
        {
            _a = a;
            _b = b;
            _r = r;
            _n = r.ALength;
            _m = r.BLength;
            MaxD = (_n + _m + 1) / 2;
            _offset = MaxD;

            // Two spare slots: the seed below sits at offset + 1, which for a 1×1 region is past
            // diff-match-patch's 2·MaxD (it special-cases that size; this does not need to).
            _v1 = new int[2 * MaxD + 2];
            _v2 = new int[2 * MaxD + 2];
            Array.Fill(_v1, -1);
            Array.Fill(_v2, -1);
            _v1[_offset + 1] = 0;
            _v2[_offset + 1] = 0;

            _delta = _n - _m;
            _front = (_delta & 1) != 0;
        }

        public int MaxD { get; }

        public long TakeSteps()
        {
            var steps = _steps;
            _steps = 0;
            return steps;
        }

        public bool Forward(int d, out int splitA, out int splitB)
        {
            for (var k1 = -d + _k1Start; k1 <= d - _k1End; k1 += 2)
            {
                var k1Offset = _offset + k1;
                var x1 = k1 == -d || (k1 != d && _v1[k1Offset - 1] < _v1[k1Offset + 1])
                    ? _v1[k1Offset + 1]
                    : _v1[k1Offset - 1] + 1;
                var y1 = x1 - k1;
                while (x1 < _n && y1 < _m && _a[_r.ALo + x1] == _b[_r.BLo + y1])
                {
                    x1++;
                    y1++;
                    _steps++;
                }
                _steps++;
                _v1[k1Offset] = x1;

                if (x1 > _n)
                {
                    _k1End += 2;
                }
                else if (y1 > _m)
                {
                    _k1Start += 2;
                }
                else if (_front && MeetsReverse(_offset + _delta - k1, x1))
                {
                    splitA = _r.ALo + x1;
                    splitB = _r.BLo + y1;
                    return true;
                }
            }
            splitA = splitB = 0;
            return false;
        }

        public bool Reverse(int d, out int splitA, out int splitB)
        {
            for (var k2 = -d + _k2Start; k2 <= d - _k2End; k2 += 2)
            {
                var k2Offset = _offset + k2;
                var x2 = k2 == -d || (k2 != d && _v2[k2Offset - 1] < _v2[k2Offset + 1])
                    ? _v2[k2Offset + 1]
                    : _v2[k2Offset - 1] + 1;
                var y2 = x2 - k2;
                while (x2 < _n && y2 < _m && _a[_r.AHi - 1 - x2] == _b[_r.BHi - 1 - y2])
                {
                    x2++;
                    y2++;
                    _steps++;
                }
                _steps++;
                _v2[k2Offset] = x2;

                if (x2 > _n)
                {
                    _k2End += 2;
                }
                else if (y2 > _m)
                {
                    _k2Start += 2;
                }
                else if (!_front && MeetsForward(_offset + _delta - k2, _n - x2, out splitA, out splitB))
                {
                    return true;
                }
            }
            splitA = splitB = 0;
            return false;
        }

        private bool MeetsReverse(int k2Offset, int x1) =>
            k2Offset >= 0 && k2Offset < _v2.Length && _v2[k2Offset] != -1 && x1 >= _n - _v2[k2Offset];

        private bool MeetsForward(int k1Offset, int mirroredX2, out int splitA, out int splitB)
        {
            splitA = splitB = 0;
            if (k1Offset < 0 || k1Offset >= _v1.Length || _v1[k1Offset] == -1)
            {
                return false;
            }

            var x1 = _v1[k1Offset];
            var y1 = _offset + x1 - k1Offset;
            if (x1 < mirroredX2)
            {
                return false;
            }
            splitA = _r.ALo + x1;
            splitB = _r.BLo + y1;
            return true;
        }
    }
}

/// <summary>
/// Where a block of lines occurs in a file. Searches anchor on the block's rarest line, so a block
/// that opens with a brace costs no more to find than one that opens with a method signature.
/// </summary>
public sealed class LineIndex
{
    private readonly int[] _ids;
    private readonly Dictionary<int, List<int>> _positions = new();

    public LineIndex(int[] ids)
    {
        _ids = ids;
        for (var i = 0; i < ids.Length; i++)
        {
            if (!_positions.TryGetValue(ids[i], out var list))
            {
                list = new List<int>();
                _positions[ids[i]] = list;
            }
            list.Add(i);
        }
    }

    /// <summary>How many times the line occurs.</summary>
    public int Count(int id) => _positions.TryGetValue(id, out var positions) ? positions.Count : 0;

    /// <summary>
    /// Start of a contiguous occurrence of <paramref name="block"/>, the one nearest
    /// <paramref name="near"/> when there are several; -1 when there is none.
    /// </summary>
    public int Find(ReadOnlySpan<int> block, int near)
    {
        var best = -1;
        var bestDistance = long.MaxValue;
        foreach (var start in Occurrences(block))
        {
            var distance = Math.Abs((long)start - near);
            if (distance < bestDistance)
            {
                best = start;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>How many contiguous occurrences of <paramref name="block"/> there are.</summary>
    public int Count(ReadOnlySpan<int> block) => Occurrences(block).Count;

    /// <summary>Start of every contiguous occurrence of <paramref name="block"/>, in order.</summary>
    public List<int> Occurrences(ReadOnlySpan<int> block)
    {
        var starts = new List<int>();
        if (block.Length == 0 || block.Length > _ids.Length)
        {
            return starts;
        }

        var anchor = -1;
        List<int>? anchorPositions = null;
        for (var k = 0; k < block.Length; k++)
        {
            if (!_positions.TryGetValue(block[k], out var positions))
            {
                return starts;
            }
            if (anchorPositions is null || positions.Count < anchorPositions.Count)
            {
                anchor = k;
                anchorPositions = positions;
            }
        }

        foreach (var position in anchorPositions!)
        {
            var start = position - anchor;
            if (start >= 0 && start + block.Length <= _ids.Length && _ids.AsSpan(start, block.Length).SequenceEqual(block))
            {
                starts.Add(start);
            }
        }
        return starts;
    }
}
