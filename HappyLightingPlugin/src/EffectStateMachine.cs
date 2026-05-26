namespace HappyLightingPlugin;

public sealed class EffectStateMachine
{
    private EffectDecision _current = new(LightFrame.Off, "Off", EffectPriority.Off, "startup", DateTimeOffset.MinValue);
    private DateTimeOffset _lastSwitchAt = DateTimeOffset.MinValue;

    public EffectDecision Update(EffectDecision candidate, PluginSettings settings, DateTimeOffset now)
    {
        var debounce = TimeSpan.FromMilliseconds(Math.Max(0, settings.EffectDebounceMs));
        var minActive = TimeSpan.FromMilliseconds(Math.Max(0, settings.EffectMinActiveMs));

        if (_current.EffectId == candidate.EffectId)
        {
            _current = candidate with { HoldUntil = _lastSwitchAt + minActive };
            return _current;
        }

        var withinDebounce = now - _lastSwitchAt < debounce;
        var withinMinActive = now < _lastSwitchAt + minActive;
        var candidateMoreCritical = candidate.Priority < _current.Priority;

        if ((withinDebounce || withinMinActive) && !candidateMoreCritical)
            return _current;

        _lastSwitchAt = now;
        _current = candidate with { HoldUntil = now + minActive };
        return _current;
    }

    public EffectDecision Current => _current;
}
