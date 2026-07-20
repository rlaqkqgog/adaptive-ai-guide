using System;
using UnityEngine;

/// <summary>
/// The single output path for an approved AAG utterance request.
/// Development uses configured text; participant sessions later use AudioClip.
/// </summary>
[DisallowMultipleComponent]
public sealed class AAGUtterancePlayer : MonoBehaviour
{
    [Serializable]
    private sealed class ClipLifecycleLog
    {
        public string type;
        public float t;
        public string clipId;
        public string outputMode;
        public float configuredDurationSeconds;
        public float playedSeconds;
        public string reason;
    }

    [Header("Scene Outputs")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AAGCaptionPresenter captionPresenter;

    [Header("Last Output (Play Mode)")]
    [SerializeField] private string lastOutputMode = "none";
    [SerializeField] private string lastClipId = string.Empty;
    [SerializeField, TextArea(2, 3)] private string lastCaption = string.Empty;

    private Fp1ExperimentConfig config;
    private LoggingManager loggingManager;
    private bool outputActive;
    private string activeClipId = string.Empty;
    private string activeOutputMode = string.Empty;
    private float activeStartedAt;
    private float activeDurationSeconds;
    private float activeExpectedEndAt;

    public void BeginSession(Fp1ExperimentConfig sessionConfig, LoggingManager logger)
    {
        config = sessionConfig;
        loggingManager = logger;
        StopAndReset();
    }

    private void Update()
    {
        if (!outputActive || loggingManager == null || !loggingManager.WriterOpen) return;
        var now = loggingManager.SessionTime;
        if (string.Equals(activeOutputMode, "audio", StringComparison.Ordinal)
            && now > activeStartedAt + 0.05f
            && now + 0.02f < activeExpectedEndAt
            && (audioSource == null || !audioSource.isPlaying))
        {
            InterruptActiveOutput("audio_stopped_early");
            return;
        }
        if (now + 0.02f < activeExpectedEndAt) return;
        FinishActiveOutput("duration_elapsed");
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
            BeginActiveOutput(clipId, outputMode, config.captionVisibleSeconds);
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
        BeginActiveOutput(clipId, outputMode, binding.clip.length);
        reason = "played_audio";
        return true;
    }

    public void StopAndReset(string interruptionReason = "guide_reset")
    {
        if (outputActive) InterruptActiveOutput(interruptionReason);
        if (audioSource != null) audioSource.Stop();
        if (captionPresenter != null) captionPresenter.HideImmediate();
        lastOutputMode = "none";
        lastClipId = string.Empty;
        lastCaption = string.Empty;
    }

    private void BeginActiveOutput(string clipId, string outputMode, float durationSeconds)
    {
        if (outputActive) InterruptActiveOutput("superseded");
        outputActive = true;
        activeClipId = clipId ?? string.Empty;
        activeOutputMode = outputMode ?? string.Empty;
        activeStartedAt = loggingManager != null ? loggingManager.SessionTime : 0f;
        activeDurationSeconds = Mathf.Max(0f, durationSeconds);
        activeExpectedEndAt = activeStartedAt + activeDurationSeconds;
    }

    private void FinishActiveOutput(string reason)
    {
        WriteLifecycle("clip_finished", reason);
        ClearActiveOutput();
    }

    private void InterruptActiveOutput(string reason)
    {
        WriteLifecycle("clip_interrupted", reason);
        ClearActiveOutput();
    }

    private void WriteLifecycle(string type, string reason)
    {
        if (loggingManager == null || !loggingManager.WriterOpen) return;
        var now = loggingManager.SessionTime;
        loggingManager.Write(new ClipLifecycleLog
        {
            type = type,
            t = now,
            clipId = activeClipId,
            outputMode = activeOutputMode,
            configuredDurationSeconds = activeDurationSeconds,
            playedSeconds = Mathf.Max(0f, now - activeStartedAt),
            reason = reason ?? string.Empty,
        }, type);
    }

    private void ClearActiveOutput()
    {
        outputActive = false;
        activeClipId = string.Empty;
        activeOutputMode = string.Empty;
        activeStartedAt = 0f;
        activeDurationSeconds = 0f;
        activeExpectedEndAt = 0f;
    }

    private static int LevelNumber(AagSupportLevel level)
    {
        return Mathf.Clamp((int)level + 1, 1, 5);
    }
}
