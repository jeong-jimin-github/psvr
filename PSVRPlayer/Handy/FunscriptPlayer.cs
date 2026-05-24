using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PSVRPlayer.Handy;

/// <summary>Funscript parser and per-frame position interpolator.</summary>
public sealed class FunscriptPlayer
{
    public record Action(long At, int Pos); // At = milliseconds, Pos = 0-100

    private List<Action> _actions = new();
    public  bool Loaded => _actions.Count > 0;
    public  IReadOnlyList<Action> Actions => _actions;
    public  long DurationMs => _actions.Count > 0 ? _actions[^1].At : 0;

    public void Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("actions", out var arr))
            throw new FormatException("actionsフィールドが見つかりません");

        _actions = arr.EnumerateArray()
            .Select(e => new Action(e.GetProperty("at").GetInt64(), e.GetProperty("pos").GetInt32()))
            .OrderBy(a => a.At)
            .ToList();
    }

    public void Clear() => _actions.Clear();

    /// <summary>Linear-interpolated position (0-100) at <paramref name="timeMsec"/>.</summary>
    public int GetPositionAt(long timeMsec)
    {
        if (_actions.Count == 0) return 0;
        if (timeMsec <= _actions[0].At)  return _actions[0].Pos;
        if (timeMsec >= _actions[^1].At) return _actions[^1].Pos;

        int lo = 0, hi = _actions.Count - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) >> 1;
            if (_actions[mid].At <= timeMsec) lo = mid; else hi = mid;
        }

        var a = _actions[lo]; var b = _actions[hi];
        double t = (double)(timeMsec - a.At) / (b.At - a.At);
        return (int)Math.Round(a.Pos + (b.Pos - a.Pos) * t);
    }
}
