using System.Globalization;

namespace LeanStudio.Core.Git;

/// <summary>When the <c>sorry</c>s might run out, from how fast they have been proved.</summary>
/// <param name="Remaining">The sorries there are now.</param>
/// <param name="SlopePerDay">The trend in the last commits: sorries per day (negative is progress).</param>
/// <param name="Projected">The day the trend reaches zero, or null when there is no downward trend.</param>
/// <param name="RSquared">How well a straight line fits the history, 0 to 1.</param>
/// <param name="Confidence"><c>high</c>, <c>medium</c> or <c>low</c>, from how well a line fits.</param>
/// <param name="Message">All of it in a sentence.</param>
public sealed record Forecast(int Remaining, double SlopePerDay, DateOnly? Projected, double RSquared, string Confidence, string Message)
{
    /// <summary>
    /// A forecast from <paramref name="points"/> (oldest first), by a straight line through the last <paramref name="window"/>
    /// of them. A rough guide: a formalization's pace is lumpy, and the line says so in its confidence.
    /// </summary>
    public static Forecast From(IReadOnlyList<SorryPoint> points, int window = 30)
    {
        List<(double Day, double Sorries)> xs = [.. points.TakeLast(Math.Max(2, window))
            .Select(p => (Day: (double)DateOnly.ParseExact(p.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber, Sorries: (double)p.Sorries))];
        if (xs.Count < 3 || xs[^1].Day - xs[0].Day < 1)
        {
            int now = points.Count > 0 ? points[^1].Sorries : 0;
            return new Forecast(now, 0, null, 0, "low", "Too little history to say: a forecast needs at least three commits over more than a day.");
        }
        double n = xs.Count, mx = xs.Average(x => x.Day), my = xs.Average(x => x.Sorries);
        double sxx = xs.Sum(x => (x.Day - mx) * (x.Day - mx)), sxy = xs.Sum(x => (x.Day - mx) * (x.Sorries - my)), syy = xs.Sum(x => (x.Sorries - my) * (x.Sorries - my));
        double slope = sxy / sxx;
        double r2 = syy == 0 ? 0 : sxy * sxy / (sxx * syy);
        int remaining = (int)xs[^1].Sorries;
        string confidence = r2 >= 0.8 ? "high" : r2 >= 0.5 ? "medium" : "low";
        string span = $"the last {xs.Count} commits ({(xs[^1].Day - xs[0].Day).ToString("0", CultureInfo.InvariantCulture)} days)";
        if (remaining == 0)
        {
            return new Forecast(0, slope, null, r2, confidence, "There are no sorries left.");
        }
        if (slope > -0.005)
        {
            return new Forecast(remaining, slope, null, r2, confidence, $"{remaining} sorries remain and over {span} their number has not been falling ({slope.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} a day), so there is no pace to forecast from.");
        }
        DateOnly last = DateOnly.FromDayNumber((int)xs[^1].Day);
        DateOnly done = last.AddDays((int)Math.Ceiling(remaining / -slope));
        return new Forecast(remaining, slope, done, r2, confidence,
            $"{remaining} sorries remain. At the pace of {span}, {(-slope).ToString("0.00", CultureInfo.InvariantCulture)} proved a day, the last would go around {done:yyyy-MM-dd} ({confidence} confidence: a line fits the history with R² {r2.ToString("0.00", CultureInfo.InvariantCulture)}).");
    }
}
