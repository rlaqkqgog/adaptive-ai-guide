using UnityEngine;

/// <summary>
/// The single output path for an approved AAG utterance request.
/// Development uses configured text; participant sessions later use AudioClip.
/// </summary>
[DisallowMultipleComponent]
public sealed class AAGUtterancePlayer : MonoBehaviour
{
    [Header("Scene Outputs")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AAGCaptionPresenter captionPresenter;

    [Header("Last Output (Play Mode)")]
    [SerializeField] private string lastOutputMode = "none";
    [SerializeField] private string lastClipId = string.Empty;
    [SerializeField, TextArea(2, 3)] private string lastCaption = string.Empty;

    private Fp1ExperimentConfig config;

    public void BeginSession(Fp1ExperimentConfig sessionConfig)
    {
        config = sessionConfig;
        StopAndReset();
    }

    public bool TryPlay(
        string clipId,
        AagSupportLevel level,
        out string outputMode,
        out string reason)
    {
        outputMode = "none";
        reason = "output_disabled";

        if (config == null)
        {
            reason = "config_missing";
            return false;
        }

        var binding = config.FindAagClip(clipId);
        if (binding == null)
        {
            reason = "clip_binding_missing";
            return false;
        }

        if (config.aagTextModeEnabled)
        {
            if (captionPresenter == null)
            {
                reason = "caption_presenter_missing";
                return false;
            }
            if (string.IsNullOrWhiteSpace(binding.captionText))
            {
                reason = "caption_text_missing";
                return false;
            }

            lastClipId = clipId;
            lastOutputMode = outputMode = "text";
            var levelLabel = clipId.StartsWith("GF-") ? "GATE" : $"{LevelNumber(level)}/5 {level}";
            lastCaption = $"[{clipId} | {levelLabel}] {binding.captionText}";
            captionPresenter.ShowCaption(lastCaption, config.captionVisibleSeconds);
            reason = "played_text";
            return true;
        }

        if (!config.aagPlaybackEnabled)
        {
            reason = "decision_only";
            return false;
        }
        if (audioSource == null)
        {
            reason = "audio_source_missing";
            return false;
        }
        if (audioSource.isPlaying)
        {
            reason = "audio_busy";
            return false;
        }
        if (binding.clip == null)
        {
            reason = "audio_clip_missing";
            return false;
        }

        lastClipId = clipId;
        lastOutputMode = outputMode = "audio";
        lastCaption = binding.captionText ?? string.Empty;
        audioSource.clip = binding.clip;
        audioSource.Play();
        reason = "played_audio";
        return true;
    }

    public void StopAndReset()
    {
        if (audioSource != null) audioSource.Stop();
        if (captionPresenter != null) captionPresenter.HideImmediate();
        lastOutputMode = "none";
        lastClipId = string.Empty;
        lastCaption = string.Empty;
    }

    private static int LevelNumber(AagSupportLevel level)
    {
        return Mathf.Clamp((int)level + 1, 1, 5);
    }
}
