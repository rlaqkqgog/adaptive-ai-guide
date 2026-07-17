using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// Displays a short, head-locked AAG caption. It owns presentation only;
/// AAGGuide remains responsible for deciding whether an utterance is played.
/// </summary>
[DisallowMultipleComponent]
public sealed class AAGCaptionPresenter : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private CanvasGroup panelGroup;
    [SerializeField] private TextMeshProUGUI captionText;
    [SerializeField] private TMP_FontAsset captionFont;

    [Header("Display")]
    [SerializeField, Min(0.1f)] private float defaultVisibleSeconds = 3f;

    [Header("Last Caption (Play Mode)")]
    [SerializeField] private bool visible;
    [SerializeField, TextArea(2, 3)] private string lastCaption = string.Empty;

    private Coroutine hideRoutine;

    private void Awake()
    {
        ConfigureNonBlockingUi();
        HideImmediate();
    }

    private void OnDisable()
    {
        if (hideRoutine != null)
        {
            StopCoroutine(hideRoutine);
            hideRoutine = null;
        }

        SetVisible(false);
    }

    public void ShowCaption(string message)
    {
        ShowCaption(message, defaultVisibleSeconds);
    }

    public void ShowCaption(string message, float visibleSeconds)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            HideImmediate();
            return;
        }

        ConfigureNonBlockingUi();

        if (hideRoutine != null) StopCoroutine(hideRoutine);

        lastCaption = message;
        captionText.text = message;
        SetVisible(true);
        hideRoutine = StartCoroutine(HideAfter(Mathf.Max(0.1f, visibleSeconds)));
    }

    public void HideImmediate()
    {
        if (hideRoutine != null)
        {
            StopCoroutine(hideRoutine);
            hideRoutine = null;
        }

        if (captionText != null) captionText.text = string.Empty;
        SetVisible(false);
    }

    private IEnumerator HideAfter(float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        hideRoutine = null;
        if (captionText != null) captionText.text = string.Empty;
        SetVisible(false);
    }

    private void ConfigureNonBlockingUi()
    {
        if (panelGroup != null)
        {
            panelGroup.interactable = false;
            panelGroup.blocksRaycasts = false;
        }

        if (captionText != null)
        {
            captionText.raycastTarget = false;
            if (captionFont != null) captionText.font = captionFont;
        }
    }

    private void SetVisible(bool value)
    {
        visible = value;
        if (panelGroup != null) panelGroup.alpha = value ? 1f : 0f;
    }
}
