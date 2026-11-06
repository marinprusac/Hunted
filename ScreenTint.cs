using UnityEngine;
using UnityEngine.UI;

namespace Hunted;

/// <summary>
/// A colour over the local player's whole screen: the game's own coloured screen edge (its vignette, as the ceiling eye uses) and a light wash of the same colour
/// over the rest. Whatever wants it asks for it every frame (the Everseer's mask, a survivor's boost); it fades in, and out once nobody asks any more.
/// </summary>
internal static class ScreenTint
{
    private static GameObject? _object;
    private static Image? _image;
    private static Color _wanted;
    private static float _wantedAlpha;
    private static int _askedFrame = -1;

    /// <summary>Asks for the tint this frame: <paramref name="wash"/> is how strong the wash is (0 to 1), <paramref name="edge"/> the screen edge's.</summary>
    internal static void Request(Color colour, float wash, float edge)
    {
        _wanted = colour;
        _wantedAlpha = wash;
        _askedFrame = Time.frameCount;
        if (PostProcessing.Instance != null && Hunted.Instance != null)
            PostProcessing.Instance.VignetteOverride(colour, edge, 1f, 3f, 3f, 0.1f, Hunted.Instance.gameObject, 900);
    }

    internal static void Tick(float dt)
    {
        var asked = _askedFrame >= Time.frameCount - 1;
        if (_object == null)
        {
            if (!asked)
                return;
            _object = new GameObject("Hunted Screen Tint", typeof(RectTransform), typeof(Canvas), typeof(Image));
            Object.DontDestroyOnLoad(_object);
            var canvas = _object.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -100;
            _image = _object.GetComponent<Image>();
            _image.raycastTarget = false;
            _image.color = Color.clear;
            var rect = (RectTransform)_image.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
        if (_image == null)
            return;
        var alpha = Mathf.MoveTowards(_image.color.a, asked ? _wantedAlpha : 0f, dt * 2f);
        _image.color = new Color(_wanted.r, _wanted.g, _wanted.b, alpha);
        _image.enabled = alpha > 0f;
    }
}
