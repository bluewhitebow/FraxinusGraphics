namespace UnityEditor.Rendering.Universal
{
    /// <summary>
    /// Correctly spelled source-compatible alias for
    /// <see cref="ShadowShape2DProvider_ProperyDrawer"/>.
    /// </summary>
    /// <remarks>
    /// The canonical Unity 6 type retains the historical "Propery" typo and
    /// owns the CustomPropertyDrawer registration. This alias intentionally
    /// has no drawer attribute so Unity discovers only one provider drawer.
    /// </remarks>
    public class ShadowShape2DProviderPropertyDrawer : ShadowShape2DProvider_ProperyDrawer
    {
    }
}
