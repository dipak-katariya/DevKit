using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>One block of a change at a time: where it stands on the branch, and what to show about it.</summary>
public static partial class CodePresenceAnalyzer
{
    /// <summary>Where one block of the change stands on the branch, before it is dressed up for display.</summary>
    private readonly record struct Finding(HunkStatus Status, int? DestinationIndex, string Note)
    {
        public bool IsPresent => Status is HunkStatus.Present or HunkStatus.PresentAdjusted or HunkStatus.MatchesQa;
    }

    /// <summary>
    /// How QA moved on from the pull request's own lines: the span of QA's current file that now stands in
    /// their place, and how many unchanged lines sit either side of it. Only a QA edit of those lines
    /// counts — an edit next to them is not a later version of them.
    /// </summary>
    private readonly record struct QaArea(bool Changed, int Start, int Length, int ContextBefore, int ContextAfter)
    {
        /// <summary>Maps <c>after[start..end)</c> through the after → QA-now diff.</summary>
        public static QaArea Of(IReadOnlyList<DiffHunk> qaHunks, int start, int end, int qaCount)
        {
            int first = -1, last = -1, x0 = start, x1 = end, shiftBefore = 0, shiftInside = 0;
            for (var i = 0; i < qaHunks.Count; i++)
            {
                var q = qaHunks[i];
                if (Changes(q, start, end))
                {
                    first = first < 0 ? i : first;
                    last = i;
                    x0 = Math.Min(x0, q.OldStart);
                    x1 = Math.Max(x1, q.OldEnd);
                    shiftInside += q.NewLength - q.OldLength;
                }
                else if (first < 0 && LiesBefore(q, start, end))
                {
                    shiftBefore += q.NewLength - q.OldLength;
                }
                else if (first >= 0)
                {
                    break;
                }
            }
            if (first < 0)
            {
                return default;
            }

            var y0 = x0 + shiftBefore;
            var y1 = x1 + shiftBefore + shiftInside;
            var previousEnd = first > 0 ? qaHunks[first - 1].NewEnd : 0;
            var nextStart = last + 1 < qaHunks.Count ? qaHunks[last + 1].NewStart : qaCount;
            return new QaArea(true, y0, y1 - y0, y0 - previousEnd, nextStart - y1);
        }

        /// <summary>
        /// Whether a QA edit changes the pull request's own lines. A pull request that only deleted lines
        /// left none of its own, so there only QA putting lines back at exactly that point counts.
        /// </summary>
        private static bool Changes(DiffHunk q, int start, int end) =>
            start < end
                ? q.OldStart < end && q.OldEnd > start
                : q.OldStart == start && q.OldEnd == start;

        private static bool LiesBefore(DiffHunk q, int start, int end) =>
            q.OldEnd < start || (q.OldEnd == start && (q.OldStart < start || start < end));
    }

    /// <summary>Everything one file's blocks are checked against, so each block's check is one call.</summary>
    private sealed class HunkScope
    {
        private readonly LineInterner _interner;
        private readonly LineSequence _before;
        private readonly LineSequence _after;
        private readonly LineSequence _destination;
        private readonly LineSequence? _qa;
        private readonly LineIndex _destinationIndex;
        private readonly LineIndex _beforeIndex;
        private readonly LineIndex _afterIndex;
        private readonly IReadOnlyList<DiffHunk> _hunks;

        // Branch lines already credited to a block, so one copy cannot vouch for two identical blocks.
        // A block that only removed lines is credited with the join between its context lines instead.
        private readonly HashSet<int> _claimedLines = new();
        private readonly HashSet<int> _claimedJoins = new();
        private IReadOnlyList<DiffHunk>? _afterToQa;

        public HunkScope(LineInterner interner, LineSequence before, LineSequence after,
            LineSequence destination, IReadOnlyList<DiffHunk> hunks, LineSequence? qa)
        {
            _interner = interner;
            _before = before;
            _after = after;
            _destination = destination;
            _hunks = hunks;
            _qa = qa;
            _destinationIndex = new LineIndex(destination.Ids);
            _beforeIndex = new LineIndex(before.Ids);
            _afterIndex = new LineIndex(after.Ids);
        }

        public bool Approximate { get; private set; }

        public bool IsTrivial(int hunkIndex)
        {
            var hunk = _hunks[hunkIndex];
            return !_interner.AnySignificant(_after.Ids.AsSpan(hunk.NewStart, hunk.NewLength))
                   && !_interner.AnySignificant(_before.Ids.AsSpan(hunk.OldStart, hunk.OldLength));
        }

        public Finding Find(int hunkIndex)
        {
            var hunk = _hunks[hunkIndex];
            var (before, after) = ContextAround(hunkIndex);
            var finding = Locate(hunk, before, after);
            return finding.IsPresent || _qa is null ? finding : RecheckOnQa(hunk, finding);
        }

        public HunkCheck Build(int hunkIndex, Finding finding)
        {
            var hunk = _hunks[hunkIndex];
            var (before, after) = ContextAround(hunkIndex);
            var lines = new List<CodeLine>();
            AddLines(lines, _after, hunk.NewStart - before, hunk.NewStart, CodeLineKind.Context);
            AddLines(lines, _before, hunk.OldStart, hunk.OldEnd, CodeLineKind.Removed);
            AddLines(lines, _after, hunk.NewStart, hunk.NewEnd, CodeLineKind.Added);
            AddLines(lines, _after, hunk.NewEnd, hunk.NewEnd + after, CodeLineKind.Context);

            var shown = lines.Count <= MaxPreviewLines ? lines : lines.GetRange(0, MaxPreviewLines);
            return new HunkCheck
            {
                Status = finding.Status,
                Note = finding.Note,
                QaLine = _after.NumberAt(hunk.NewStart),
                DestinationLine = finding.DestinationIndex is int at ? _destination.NumberAt(at) : null,
                Added = hunk.NewLength,
                Removed = hunk.OldLength,
                Lines = shown,
                HiddenLines = lines.Count - shown.Count
            };
        }

        /// <summary>Unchanged lines available either side of a block, never reaching into a neighbour.</summary>
        private (int Before, int After) ContextAround(int hunkIndex)
        {
            var hunk = _hunks[hunkIndex];
            var previousEnd = hunkIndex > 0 ? _hunks[hunkIndex - 1].NewEnd : 0;
            var nextStart = hunkIndex + 1 < _hunks.Count ? _hunks[hunkIndex + 1].NewStart : _after.Count;
            return (Math.Min(MaxContext, hunk.NewStart - previousEnd), Math.Min(MaxContext, nextStart - hunk.NewEnd));
        }

        private Finding Locate(DiffHunk hunk, int contextBefore, int contextAfter)
        {
            var near = Near(hunk.NewStart, _after.Count);
            var tried = (-1, -1);
            for (var k = MaxContext; k >= 1; k--)
            {
                var (b, a) = (Math.Min(k, contextBefore), Math.Min(k, contextAfter));
                if ((b, a) == tried || b + a == 0)
                {
                    continue;
                }
                tried = (b, a);

                var at = Claim(_after.Ids.AsSpan(hunk.NewStart - b, hunk.NewLength + b + a), b, hunk.NewLength, near);
                if (at >= 0)
                {
                    return new Finding(HunkStatus.Present, at + b, "");
                }

                at = StillThere(_before.Ids.AsSpan(hunk.OldStart - b, hunk.OldLength + b + a), near);
                if (at >= 0)
                {
                    return new Finding(HunkStatus.Missing, at + b,
                        "The branch still has the code from before this pull request here.");
                }
            }
            return LocateWithoutContext(hunk, near);
        }

        private Finding LocateWithoutContext(DiffHunk hunk, int near)
        {
            var added = _after.Ids.AsSpan(hunk.NewStart, hunk.NewLength);
            if (_interner.AnySignificant(added))
            {
                var at = Claim(added, 0, added.Length, near);
                return at >= 0
                    ? new Finding(HunkStatus.PresentAdjusted, at, "These lines are on the branch, but the code around them is different.")
                    : FromAddedLines(added);
            }

            // Nothing meaningful was added, so the block is judged by what it took away — which, as the
            // block is not trivial, is something meaningful.
            return FromRemovedLines(_before.Ids.AsSpan(hunk.OldStart, hunk.OldLength));
        }

        /// <summary>For added lines that are not on the branch as a run: how many of them are, one by one.</summary>
        private Finding FromAddedLines(ReadOnlySpan<int> added)
        {
            int significant = 0, found = 0;
            foreach (var id in added)
            {
                if (_interner.IsSignificant(id))
                {
                    significant++;
                    found += Gained(id) ? 1 : 0;
                }
            }

            if (found == 0)
            {
                return new Finding(HunkStatus.Missing, null, "None of the lines this pull request added are on the branch.");
            }
            return new Finding(HunkStatus.Altered, null, found == significant
                ? $"All {significant} added lines are on the branch, but not together the way this pull request wrote them."
                : $"{found} of {significant} added lines are on the branch; the rest are missing or were changed.");
        }

        /// <summary>
        /// For a block that removed code: whether those lines are gone, one by one. Gone as a whole run is
        /// not enough — a resolution that kept half of them breaks the run without removing the code.
        /// </summary>
        private Finding FromRemovedLines(ReadOnlySpan<int> removed)
        {
            int significant = 0, survivors = 0;
            foreach (var id in removed)
            {
                if (_interner.IsSignificant(id))
                {
                    significant++;
                    survivors += Survives(id) ? 1 : 0;
                }
            }

            if (survivors == 0)
            {
                return new Finding(HunkStatus.PresentAdjusted, null, "The lines this pull request removed are no longer on the branch.");
            }
            return survivors == significant
                ? new Finding(HunkStatus.Missing, null, "The lines this pull request removed are still on the branch.")
                : new Finding(HunkStatus.Altered, null, $"{survivors} of {significant} lines this pull request removed are still on the branch.");
        }

        private Finding RecheckOnQa(DiffHunk hunk, Finding finding)
        {
            var qa = _qa!;
            var area = QaArea.Of(AfterToQa(qa), hunk.NewStart, hunk.NewEnd, qa.Count);
            if (!area.Changed)
            {
                // QA still has these lines exactly as the pull request left them, so the branch really lacks them.
                return finding;
            }

            if (area.Length == 0)
            {
                // QA took the pull request's lines out again. The branch matches QA only if it has neither
                // those lines nor the ones the pull request replaced.
                var added = _after.Ids.AsSpan(hunk.NewStart, hunk.NewLength);
                var removed = _before.Ids.AsSpan(hunk.OldStart, hunk.OldLength);
                return (_interner.AnySignificant(added) && Gained(added)) || AnySurvives(removed)
                    ? finding
                    : new Finding(HunkStatus.MatchesQa, null, "QA removed or reverted these lines later, and the branch doesn't have them either.");
            }

            var near = Near(area.Start, qa.Count);
            var tried = (-1, -1);
            for (var k = MaxContext; k >= 0; k--)
            {
                var (b, a) = (Math.Min(k, area.ContextBefore), Math.Min(k, area.ContextAfter));
                if ((b, a) == tried)
                {
                    continue;
                }
                tried = (b, a);

                var at = Claim(qa.Ids.AsSpan(area.Start - b, area.Length + b + a), b, area.Length, near);
                if (at >= 0)
                {
                    return new Finding(HunkStatus.MatchesQa, at + b,
                        "QA changed these lines again after this pull request, and the branch has QA's current version.");
                }
            }
            return finding with { Note = finding.Note + " QA has changed these lines since, and the branch has neither version." };
        }

        private IReadOnlyList<DiffHunk> AfterToQa(LineSequence qa)
        {
            if (_afterToQa is null)
            {
                var diff = LineDiff.Compute(_after.Ids, qa.Ids);
                Approximate |= diff.Approximate;
                _afterToQa = diff.Hunks;
            }
            return _afterToQa;
        }

        /// <summary>
        /// The nearest copy of <paramref name="block"/> on the branch that is new — the branch has more
        /// copies than the file had before the pull request — and not credited to another block yet. Its
        /// core lines, <c>[coreOffset, coreOffset + coreLength)</c> of the block, are credited to this one.
        /// -1 when there is no such copy.
        /// </summary>
        private int Claim(ReadOnlySpan<int> block, int coreOffset, int coreLength, int near)
        {
            if (!_interner.AnySignificant(block))
            {
                return -1;
            }

            var free = _destinationIndex.Occurrences(block)
                .Where(start => !IsClaimed(start + coreOffset, coreLength))
                .ToList();
            if (free.Count <= _beforeIndex.Count(block))
            {
                return -1;
            }

            var chosen = free.MinBy(start => Math.Abs((long)start - near));
            ClaimRange(chosen + coreOffset, coreLength);
            return chosen;
        }

        private bool IsClaimed(int position, int length)
        {
            if (length == 0)
            {
                return _claimedJoins.Contains(position);
            }
            for (var i = position; i < position + length; i++)
            {
                if (_claimedLines.Contains(i))
                {
                    return true;
                }
            }
            return false;
        }

        private void ClaimRange(int position, int length)
        {
            if (length == 0)
            {
                _claimedJoins.Add(position);
                return;
            }
            for (var i = position; i < position + length; i++)
            {
                _claimedLines.Add(i);
            }
        }

        /// <summary>The old code, in a copy the pull request's own result does not have: still on the branch.</summary>
        private int StillThere(ReadOnlySpan<int> block, int near) =>
            _interner.AnySignificant(block) && _destinationIndex.Count(block) > _afterIndex.Count(block)
                ? _destinationIndex.Find(block, near)
                : -1;

        /// <summary>The branch has more copies of the line than the file had before the pull request.</summary>
        private bool Gained(int id) => _destinationIndex.Count(id) > _beforeIndex.Count(id);

        private bool Gained(ReadOnlySpan<int> block) => _destinationIndex.Count(block) > _beforeIndex.Count(block);

        /// <summary>The branch has more copies of the line than the pull request left: a removal not made there.</summary>
        private bool Survives(int id) => _destinationIndex.Count(id) > _afterIndex.Count(id);

        private bool AnySurvives(ReadOnlySpan<int> removed)
        {
            foreach (var id in removed)
            {
                if (_interner.IsSignificant(id) && Survives(id))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Where a position would fall on the branch if the files lined up proportionally.</summary>
        private int Near(int index, int count) =>
            (int)((long)index * _destination.Count / Math.Max(1, count));

        private void AddLines(List<CodeLine> lines, LineSequence source, int from, int to, CodeLineKind kind)
        {
            for (var i = from; i < to; i++)
            {
                var id = source.Ids[i];
                bool? onBranch = null;
                if (kind != CodeLineKind.Context && _interner.IsSignificant(id))
                {
                    onBranch = kind == CodeLineKind.Added ? Gained(id) : Survives(id);
                }
                lines.Add(new CodeLine(kind, source.Numbers[i], source.Texts[i], onBranch));
            }
        }
    }
}
