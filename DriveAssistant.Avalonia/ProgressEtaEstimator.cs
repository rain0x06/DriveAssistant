using System.Globalization;

namespace DriveAssistant.Avalonia;

internal sealed class ProgressEtaEstimator
{
    private readonly Queue<(DateTime TimestampUtc, double Percent)> _samples = new();
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private double _lastPercent = double.NaN;

    public string BuildStatus(double percent, string? stateText)
    {
        var now = DateTime.UtcNow;
        var clampedPercent = Math.Clamp(percent, 0d, 100d);
        if (double.IsNaN(_lastPercent) || clampedPercent + 0.001 < _lastPercent)
        {
            Reset(now, clampedPercent);
        }

        TrackSample(now, clampedPercent);

        var elapsed = now - _startedAtUtc;
        if (clampedPercent >= 100 || IsTerminal(stateText))
        {
            return $"Elapsed {FormatDuration(elapsed)}";
        }

        var eta = TryEstimateEta(clampedPercent);
        if (eta == null)
        {
            return $"ETA calculating, elapsed {FormatDuration(elapsed)}";
        }

        return $"ETA {FormatDuration(eta.Value)}, elapsed {FormatDuration(elapsed)}";
    }

    public string BuildIndeterminateStatus(string? stateText)
    {
        var now = DateTime.UtcNow;
        if (double.IsNaN(_lastPercent))
        {
            Reset(now, 0);
        }

        var elapsed = now - _startedAtUtc;
        if (IsTerminal(stateText))
        {
            return $"Elapsed {FormatDuration(elapsed)}";
        }

        return $"ETA unavailable, elapsed {FormatDuration(elapsed)}";
    }

    private void Reset(DateTime now, double percent)
    {
        _samples.Clear();
        _startedAtUtc = now;
        _lastPercent = percent;
    }

    private void TrackSample(DateTime now, double percent)
    {
        if (_samples.Count == 0 || percent > _lastPercent + 0.001)
        {
            _samples.Enqueue((now, percent));
        }
        else if (_samples.Count == 0)
        {
            _samples.Enqueue((now, percent));
        }

        _lastPercent = Math.Max(_lastPercent, percent);
        var cutoff = now - TimeSpan.FromSeconds(45);
        while (_samples.Count > 2 && _samples.Peek().TimestampUtc < cutoff)
        {
            _samples.Dequeue();
        }
    }

    private TimeSpan? TryEstimateEta(double percent)
    {
        if (percent <= 0.1 || _samples.Count < 2)
        {
            return null;
        }

        var first = _samples.Peek();
        var latest = _samples.Last();
        var deltaPercent = latest.Percent - first.Percent;
        var deltaSeconds = (latest.TimestampUtc - first.TimestampUtc).TotalSeconds;
        if (deltaPercent <= 0.01 || deltaSeconds <= 0.1)
        {
            return null;
        }

        var percentPerSecond = deltaPercent / deltaSeconds;
        if (percentPerSecond <= 0)
        {
            return null;
        }

        var remainingPercent = Math.Max(0, 100 - percent);
        return TimeSpan.FromSeconds(remainingPercent / percentPerSecond);
    }

    private static bool IsTerminal(string? stateText)
    {
        if (string.IsNullOrWhiteSpace(stateText))
        {
            return false;
        }

        return stateText.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("done", StringComparison.OrdinalIgnoreCase)
            || stateText.Contains("ready", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
