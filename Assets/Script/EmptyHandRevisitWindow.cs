using System;
using System.Collections.Generic;

/// <summary>
/// Empty-hand clock and revisit segments. Calling Sample with emptyHand=false is
/// a true freeze: elapsed time does not advance and no segment expires.
/// Kept independent from Unity so this critical invariant can be tested directly.
/// </summary>
public sealed class EmptyHandRevisitWindow
{
    private sealed class Segment
    {
        public float start;
        public float end;
        public bool revisit;
    }

    private readonly List<Segment> segments = new List<Segment>();
    public float TotalAccumulatedEmptyHandSeconds { get; private set; }

    public void Reset()
    {
        segments.Clear();
        TotalAccumulatedEmptyHandSeconds = 0f;
    }

    public void Sample(float deltaSeconds, bool emptyHand, bool revisit)
    {
        if (!emptyHand || deltaSeconds <= 0f) return;
        var start = TotalAccumulatedEmptyHandSeconds;
        TotalAccumulatedEmptyHandSeconds += deltaSeconds;
        if (segments.Count > 0 && segments[segments.Count - 1].revisit == revisit)
        {
            segments[segments.Count - 1].end = TotalAccumulatedEmptyHandSeconds;
        }
        else
        {
            segments.Add(new Segment { start = start, end = TotalAccumulatedEmptyHandSeconds, revisit = revisit });
        }
    }

    public void GetWindow(float windowSeconds, out float revisitSeconds, out float emptyHandSeconds)
    {
        revisitSeconds = 0f;
        emptyHandSeconds = 0f;
        var minimum = Math.Max(0f, TotalAccumulatedEmptyHandSeconds - Math.Max(0f, windowSeconds));
        foreach (var segment in segments)
        {
            var start = Math.Max(segment.start, minimum);
            var duration = Math.Max(0f, segment.end - start);
            emptyHandSeconds += duration;
            if (segment.revisit) revisitSeconds += duration;
        }
    }
}
