using LibHandheld.Contracts;

namespace LibHandheld.Internal.Devices;

/// <summary>One owner for readable and acknowledged values, retaining the time that evidence was obtained.</summary>
/// <remarks>Locked, so a state published from a transport callback can read while a command records a write.</remarks>
internal sealed class ObservedValues
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string, string?), Observation> _values = [];

    internal void Written(string id, CapabilityValue value, string? instance = null)
    {
        lock (_gate)
        {
            _values[(id, instance)] = new Observation(value, DateTimeOffset.UtcNow, true);
        }
    }

    internal void Read(string id, CapabilityValue? value, string? instance = null)
    {
        var key = (id, instance);
        lock (_gate)
        {
            if (value is null)
            {
                if (!_values.TryGetValue(key, out var previous) || !previous.Written)
                {
                    _values.Remove(key);
                }

                return;
            }

            if (_values.TryGetValue(key, out var existing) && existing.Written && !Same(existing.Value, value))
            {
                return;
            }

            _values[key] = new Observation(value, DateTimeOffset.UtcNow, false);
        }
    }

    internal CapabilityValue? Value(string id, string? instance = null)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault((id, instance))?.Value;
        }
    }

    internal DateTimeOffset? Time(string id, string? instance = null)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault((id, instance))?.At;
        }
    }

    internal void Forget(string id, string? instance = null)
    {
        lock (_gate)
        {
            _values.Remove((id, instance));
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
        }
    }
    private static bool Same(CapabilityValue left, CapabilityValue right)
    {
        return left.Kind == right.Kind
               && left.BooleanValue == right.BooleanValue && left.IntegerValue == right.IntegerValue
               && left.ChoiceValue == right.ChoiceValue && left.ColorValue == right.ColorValue
               && left.TextValue == right.TextValue && left.CurveValue.SequenceEqual(right.CurveValue);
    }

    private sealed record Observation(CapabilityValue Value, DateTimeOffset At, bool Written);
}
