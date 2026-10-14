namespace Emberlight.Core;

/// <summary>
/// Turns mouse-wheel movement into whole steps (one slide per notch). A standard wheel notch is 120 units, but
/// precision touchpads and free-spinning wheels send many smaller amounts, so these are added up until they reach a
/// notch. Reversing direction starts the count again, so a half scroll one way doesn't eat into the other.
/// </summary>
public sealed class WheelStepper
{
    /// <summary>One notch of a standard mouse wheel (Windows' WHEEL_DELTA).</summary>
    public const int Notch = 120;

    private int _pending;

    /// <summary>
    /// Adds a wheel delta (positive = scrolled up / away from the user) and returns the number of whole steps
    /// completed: negative for up, positive for down, so it can be used directly as "slides forward".
    /// </summary>
    public int Add(int delta)
    {
        if (delta == 0) return 0;
        if (_pending != 0 && Math.Sign(delta) != Math.Sign(_pending)) _pending = 0;
        _pending += delta;
        int notches = _pending / Notch;              // truncates toward zero, keeping the remainder's sign
        _pending -= notches * Notch;
        return -notches;
    }

    /// <summary>Forgets any partial movement (e.g. after the slide changed some other way).</summary>
    public void Reset() => _pending = 0;
}
