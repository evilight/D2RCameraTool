namespace D2RCameraTool;

/// <summary>
/// Named camera poses for the one-click buttons, in the same units as the camera fields
/// (radians for the angles).
/// </summary>
internal static class CameraPresets
{
    /// <summary>
    /// Over-the-shoulder starting point: nearly horizontal, no roll, pulled fully back.
    ///
    /// Pitch is the whole point. The default view sits at -0.892 rad (-51 degrees) looking
    /// down; -0.15 rad (-8.6 degrees) puts the camera at roughly eye level looking forward,
    /// which is what makes the view read as third person rather than isometric.
    ///
    /// Yaw is deliberately NOT set: it only chooses which direction you look from, so leaving
    /// it alone means the button changes the pose and nothing else, which makes before/after
    /// comparison meaningful.
    ///
    /// Height is also left alone. Its scale is not established (it is a plain scalar, not an
    /// angle) and it mainly needs trying by hand.
    ///
    /// This is a starting point for evaluation, not a tuned result. The camera has no
    /// collision, so the real question this preset answers is whether the renderer copes with
    /// a ground-level view at all.
    /// </summary>
    public const float OverShoulderPitch = -0.15f;
    public const float OverShoulderRoll = 0f;
    public const float OverShoulderZoom = 2.0f;
}
