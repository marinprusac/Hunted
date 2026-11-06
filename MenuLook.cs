using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Hunted;

/// <summary>
/// The main menu's look: a still of the killer in a dark hallway covers the game's moving background, and "HUNTED" in red sits under the R.E.P.O. logo.
/// Called whenever the main page opens. The picture lives behind every menu page and goes away as soon as the main menu level is left.
/// </summary>
internal static class MenuLook
{
    private static Texture2D? _picture;
    private static GameObject? _background;

    internal static void Apply(MenuPageMain page)
    {
        AddBackground(page);
        AddTitle(page);
    }

    private static void AddBackground(MenuPageMain page)
    {
        SilenceScene();
        if (_background != null || MenuHolder.instance == null)
            return;
        var picture = Picture();
        var canvas = page.GetComponentInParent<Canvas>();
        if (picture == null || canvas == null)
            return;
        var root = canvas.rootCanvas.transform;
        // The branch of the screen's canvas that holds the menu pages: the picture goes right before it, so the pages draw over it.
        var branch = MenuHolder.instance.transform;
        while (branch.parent != null && branch.parent != root)
            branch = branch.parent;
        _background = new GameObject("Hunted Menu Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage)) { layer = branch.gameObject.layer };
        var rect = (RectTransform)_background.transform;
        rect.SetParent(root, false);
        rect.SetSiblingIndex(branch.parent == root ? branch.GetSiblingIndex() : 0);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        var fitter = _background.AddComponent<AspectRatioFitter>(); // covers the whole screen, cutting the edges rather than squeezing the picture
        fitter.aspectRatio = (float)picture.width / picture.height;
        fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        var image = _background.GetComponent<RawImage>();
        image.texture = picture;
        image.raycastTarget = false;
        _background.AddComponent<MenuBackground>();
    }

    /// <summary>The game's menu scene behind the picture: the driving truck (with its engine and swerving sounds) is switched off. The music is stopped by MenuMusicPatch.</summary>
    private static void SilenceScene()
    {
        foreach (var truck in Object.FindObjectsByType<TruckMenuAnimated>(FindObjectsSortMode.None))
            truck.gameObject.SetActive(false);
    }

    private static void AddTitle(MenuPageMain page)
    {
        var logo = page.GetComponentInChildren<MenuLogoHalloweenOverlay>(true);
        if (logo == null)
            logo = Object.FindFirstObjectByType<MenuLogoHalloweenOverlay>();
        if (logo == null)
        {
            Hunted.Logger.LogWarning("The main menu logo was not found, so the title stays R.E.P.O.");
            return;
        }
        var logoRect = (RectTransform)logo.transform;
        if (logoRect.Find("Hunted Title") != null)
            return;
        var font = page.GetComponentInChildren<MenuButton>(true)?.buttonText?.font;

        var title = new GameObject("Hunted Title", typeof(RectTransform)) { layer = logo.gameObject.layer };
        var rect = (RectTransform)title.transform;
        rect.SetParent(logoRect, false);
        var height = logoRect.rect.height > 1f ? logoRect.rect.height * 0.42f : 40f;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(0f, height);

        var text = title.AddComponent<TextMeshProUGUI>();
        if (font != null)
            text.font = font;
        text.text = "HUNTED";
        text.fontSize = height * 0.9f;
        text.characterSpacing = 12f;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.color = new Color(0.85f, 0.08f, 0.06f);
        text.raycastTarget = false;
        Hunted.Logger.LogDebug($"Main menu title added under the logo ({logoRect.rect.width:0}x{logoRect.rect.height:0}).");
    }

    private static Texture2D? Picture()
    {
        if (_picture != null)
            return _picture;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hunted.MenuBackground.jpg");
            if (stream == null)
                return null;
            var bytes = new byte[stream.Length];
            stream.Read(bytes, 0, bytes.Length);
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (!texture.LoadImage(bytes))
                return null;
            _picture = texture;
        }
        catch (System.Exception exception)
        {
            Hunted.Logger.LogWarning($"Could not load the main menu picture: {exception.Message}");
        }
        return _picture;
    }
}

/// <summary>
/// The main menu's own music, played from the picture so both go away together once the main menu level is left. It goes through the game's music volume.
/// </summary>
internal class MenuBackground : MonoBehaviour
{
    private System.Collections.IEnumerator Start()
    {
        AudioClip? clip = null;
        yield return EmbeddedAudio.Load("Hunted.MenuMusic.ogg", loaded => clip = loaded);
        if (clip == null || this == null)
            yield break;
        var source = gameObject.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = true;
        source.spatialBlend = 0f;
        source.volume = 0.6f;
        if (AudioManager.instance != null)
            source.outputAudioMixerGroup = AudioManager.instance.MusicMasterGroup;
        source.Play();
    }

    private void Update()
    {
        if (RunManager.instance == null || !SemiFunc.IsMainMenu())
            Destroy(gameObject);
    }
}

/// <summary>No music in the main menu: whichever music player the menu uses is kept quiet there, and plays as normal everywhere else.</summary>
[HarmonyPatch]
static class MenuMusicPatch
{
    [HarmonyPrefix, HarmonyPatch(typeof(ConstantMusic), "Update")]
    private static bool ConstantMusic_Prefix(ConstantMusic __instance) => Quiet(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(LevelMusic), "Update")]
    private static bool LevelMusic_Prefix(LevelMusic __instance) => Quiet(__instance);

    private static bool Quiet(MonoBehaviour music)
    {
        if (RunManager.instance == null || !SemiFunc.IsMainMenu())
            return true;
        var source = music.GetComponent<AudioSource>();
        if (source != null && source.isPlaying)
            source.Stop();
        return false;
    }
}
