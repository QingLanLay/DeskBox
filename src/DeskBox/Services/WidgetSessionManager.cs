namespace DeskBox.Services;

public enum WidgetSessionState
{
    DesktopResting,
    RaisedSession,
    InteractionActive,
    Hidden
}

/// <summary>
/// First-pass session coordinator. It records the widget session state only;
/// window z-order and animation decisions remain owned by WidgetManager/windows.
/// </summary>
public sealed class WidgetSessionManager
{
    private static readonly TimeSpan StaleEndGraceAfterForceReset = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PostResetDepthReconcileDelay = TimeSpan.FromSeconds(3);

    private readonly Action<string>? _log;
    private readonly Action<string>? _logImportant;
    private int _interactionDepth;
    private DateTime _forceResetInteractionsUtc = DateTime.MinValue;
    private DateTime _lastSwallowedEndUtc = DateTime.MinValue;
    private bool _beginSinceLastSwallow;
    private WidgetSessionState _stateBeforeInteraction = WidgetSessionState.DesktopResting;

    public WidgetSessionManager(
        Action<string>? log = null,
        Action<string>? logImportant = null)
    {
        _log = log;
        _logImportant = logImportant;
    }

    public WidgetSessionState State { get; private set; } = WidgetSessionState.DesktopResting;
    public bool IsRaised => State == WidgetSessionState.RaisedSession || State == WidgetSessionState.InteractionActive;
    public bool IsInteractionActive => _interactionDepth > 0;

    /// <summary>
    /// Raised when IsInteractionActive flips. The display-topology gate parks
    /// restores while an interaction (drag/resize/capsule arrangement) runs so
    /// long interactions cannot burn the coordinator's retry budget (spec 5.6).
    /// </summary>
    public event Action? InteractionActiveChanged;

    private void OnInteractionDepthChanged()
    {
        try
        {
            InteractionActiveChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[WidgetSession] InteractionActiveChanged handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Watchdog escape hatch for a leaked interaction depth: the restore
    /// monitor, idle normalization, and the pinned bedding watchdog all skip
    /// while any interaction is active, and the natural resets
    /// (MarkDesktopResting/MarkHidden) only run after a restore that the leak
    /// itself is gating — a leak is therefore self-sustaining.
    /// </summary>
    public void ForceResetInteractions(string reason)
    {
        if (_interactionDepth == 0)
        {
            return;
        }

        _interactionDepth = 0;
        _forceResetInteractionsUtc = DateTime.UtcNow;
        ClearPostResetSwallowTracking();
        OnInteractionDepthChanged();
        SetState(_stateBeforeInteraction, reason);
        _log?.Invoke($"[WidgetSession] interactions force-reset reason={reason}");
    }

    public void MarkDesktopResting(string reason)
    {
        _interactionDepth = 0;
        ClearPostResetSwallowTracking();
        SetState(WidgetSessionState.DesktopResting, reason);
    }

    public void MarkRaisedSession(string reason)
    {
        if (_interactionDepth > 0)
        {
            SetState(WidgetSessionState.InteractionActive, reason);
            return;
        }

        SetState(WidgetSessionState.RaisedSession, reason);
    }

    public void MarkHidden(string reason)
    {
        _interactionDepth = 0;
        ClearPostResetSwallowTracking();
        SetState(WidgetSessionState.Hidden, reason);
    }

    public void BeginInteraction(string reason)
    {
        _beginSinceLastSwallow = true;
        if (_interactionDepth == 0 && State != WidgetSessionState.InteractionActive)
        {
            _stateBeforeInteraction = State;
        }

        _interactionDepth++;
        if (_interactionDepth == 1)
        {
            OnInteractionDepthChanged();
        }

        SetState(WidgetSessionState.InteractionActive, reason);
    }

    public void EndInteraction(string reason)
    {
        if (_interactionDepth <= 0)
        {
            _log?.Invoke($"[WidgetSession] ignored end reason={reason} state={State}");
            return;
        }

        // A watchdog force-reset may have already cleared the interaction
        // this End belongs to; letting it through would zero a NEW
        // interaction opened after the reset and reopen every gated funnel
        // mid-use. Swallow ends that would zero the depth within the grace
        // window — the watchdog's post-reset reconciliation clears the
        // resulting phantom depth once no new interaction has begun.
        if (_interactionDepth == 1 &&
            DateTime.UtcNow - _forceResetInteractionsUtc < StaleEndGraceAfterForceReset)
        {
            _lastSwallowedEndUtc = DateTime.UtcNow;
            _beginSinceLastSwallow = false;
            (_logImportant ?? _log)?.Invoke(
                $"[WidgetSession] stale end swallowed post force-reset reason={reason}");
            return;
        }

        _interactionDepth--;
        if (_interactionDepth == 0)
        {
            ClearPostResetSwallowTracking();
            OnInteractionDepthChanged();
        }

        SetState(_interactionDepth > 0 ? WidgetSessionState.InteractionActive : _stateBeforeInteraction, reason);
    }

    /// <summary>
    /// Clears a phantom depth-one left by a swallowed post-reset End: once
    /// the reconcile delay has passed with no new Begin and the foreground is
    /// consistent with no live widget interaction (checked by the caller),
    /// the depth cannot belong to a live interaction and every gated funnel
    /// should reopen. A Begin after the swallow protects the depth — it may
    /// belong to a real interaction opened since the reset.
    /// </summary>
    public bool ReconcilePostResetDepth(
        DateTime utcNow,
        bool foregroundConsistentWithNoLiveInteraction)
    {
        if (_interactionDepth != 1 ||
            _lastSwallowedEndUtc == DateTime.MinValue ||
            _beginSinceLastSwallow ||
            utcNow - _lastSwallowedEndUtc < PostResetDepthReconcileDelay ||
            !foregroundConsistentWithNoLiveInteraction)
        {
            return false;
        }

        _interactionDepth = 0;
        ClearPostResetSwallowTracking();
        OnInteractionDepthChanged();
        SetState(_stateBeforeInteraction, "post-reset-depth-reconciled");
        (_logImportant ?? _log)?.Invoke(
            $"[WidgetSession] post-reset depth reconciled state={State}");
        return true;
    }

    private void ClearPostResetSwallowTracking()
    {
        _lastSwallowedEndUtc = DateTime.MinValue;
        _beginSinceLastSwallow = false;
    }

    private void SetState(WidgetSessionState nextState, string reason)
    {
        if (State == nextState)
        {
            _log?.Invoke($"[WidgetSession] kept state={State} reason={reason} interactions={_interactionDepth}");
            return;
        }

        var previousState = State;
        State = nextState;
        _log?.Invoke($"[WidgetSession] changed {previousState} -> {nextState} reason={reason} interactions={_interactionDepth}");
    }
}
