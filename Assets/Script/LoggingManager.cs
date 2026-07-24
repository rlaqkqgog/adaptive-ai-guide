using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// The only file writer used by an FP1 session. Every producer submits a plain
/// record here so file lifetime, flushes, and the session folder stay observable.
/// </summary>
[DisallowMultipleComponent]
public sealed class LoggingManager : MonoBehaviour
{
    [Serializable]
    private sealed class ConfiguredClipLog
    {
        public string clipId;
        public float durationSeconds;
        public bool hasAudioClip;
    }

    [Serializable]
    private sealed class ConfigSnapshotLog
    {
        public string type = "config_snapshot";
        public string schema = "fp1-pilot-jsonl/v2";
        public float t;
        public string sessionId;
        public string participantId;
        public string setId;
        public string guideMode;
        public string startedAtUtc;
        public string configAsset;
        public string manifestPath;
        public float sessionTimeLimitSeconds;
        public float trackIntervalSeconds;
        public float roomMinDwellSeconds;
        public float logFlushIntervalSeconds;
        public float aesWindowSeconds;
        public float decisionIntervalSeconds;
        public float aesMinimumDistanceMeters;
        public int aesMinimumUniqueRooms;
        public float aesMinimumHeadRotationDegrees;
        public string aesGateRule = "block_only_when_distance_room_rotation_are_below_threshold_and_no_target_was_found_in_window";
        public float proxyWindowSeconds;
        public float proxyColdStartRatio;
        public float proxyLowBoundary;
        public float proxyHighBoundary;
        public float minimumUtteranceGapSeconds;
        public bool aagPlaybackEnabled;
        public bool aagTextModeEnabled;
        public float captionVisibleSeconds;
        public float stalestRecencyFloorSeconds;
        public float stalestMeaningfulDwellSeconds;
        public string initialSupportLevel;
        public string supportAdjustmentRule = "recent4_strict3of4|underload:+1_harder|optimal:0|overload:-1_easier|target_found:aes_pass+proxy_reset+votes_clear|carrying:clear|gate_blocked:adjust|clamp:VeryEasy..VeryHard";
        public bool freezeProxyWhileCarrying = true;
        public string roomSelectionRule = "unvisited_then_stalest_then_remaining_count_then_tie_order";
        public string roomTieOrder;
        public string configuredClipIds;
        public string configuredCaptions;
        public ConfiguredClipLog[] configuredClips;
    }

    [Serializable]
    private sealed class SessionStartLog
    {
        public string type = "session_start";
        public float t;
        public string sessionId;
        public string participantId;
        public string setId;
        public string guideMode;
        public string startedAtUtc;
    }

    [Serializable]
    private sealed class SystemLog
    {
        public string type = "system";
        public float t;
        public string eventName;
        public string detail;
    }

    [Header("Logging State (Play Mode)")]
    [SerializeField] private bool writerOpen;
    [SerializeField] private string currentSessionId = string.Empty;
    [SerializeField] private string currentLogPath = string.Empty;
    [SerializeField] private int writtenRowCount;
    [SerializeField] private string lastRecordType = string.Empty;
    [SerializeField] private string lastWriteTime = string.Empty;

    private StreamWriter writer;
    private float sessionClockStart;
    private float nextFlushTime;
    private float flushIntervalSeconds = 10f;

    public bool WriterOpen => writerOpen && writer != null;
    public float SessionTime => WriterOpen ? Time.realtimeSinceStartup - sessionClockStart : 0f;
    public string CurrentSessionId => currentSessionId;
    public string CurrentLogPath => currentLogPath;

    public bool OpenSession(
        string sessionId,
        string participantId,
        string setId,
        ExperimentGuideMode guideMode,
        ExperimentConfig config)
    {
        CloseSession();
        if (config == null)
        {
            Debug.LogError("[LoggingManager] Cannot open a session without FP1ExperimentConfig.", this);
            return false;
        }

        try
        {
            currentSessionId = sessionId ?? string.Empty;
            var folder = Path.Combine(
                Application.persistentDataPath,
                ExperimentSpaceRuntime.LogFolderName,
                SanitizePathPart(currentSessionId));
            Directory.CreateDirectory(folder);
            currentLogPath = Path.Combine(folder, "session.jsonl");
            writer = new StreamWriter(currentLogPath, false, new UTF8Encoding(false));
            writerOpen = true;
            writtenRowCount = 0;
            lastRecordType = string.Empty;
            lastWriteTime = string.Empty;
            sessionClockStart = Time.realtimeSinceStartup;
            flushIntervalSeconds = Mathf.Max(0.1f, config.logFlushIntervalSeconds);
            nextFlushTime = Time.realtimeSinceStartup + flushIntervalSeconds;

            var startedAtUtc = DateTime.UtcNow.ToString("O");
            Write(new ConfigSnapshotLog
            {
                t = 0f,
                sessionId = currentSessionId,
                participantId = participantId ?? string.Empty,
                setId = setId ?? string.Empty,
                guideMode = guideMode.ToString(),
                startedAtUtc = startedAtUtc,
                configAsset = config.name,
                manifestPath = AagManualAnchorSetStore.ManifestPath,
                sessionTimeLimitSeconds = config.sessionTimeLimitSeconds,
                trackIntervalSeconds = config.trackIntervalSeconds,
                roomMinDwellSeconds = config.roomMinDwellSeconds,
                logFlushIntervalSeconds = config.logFlushIntervalSeconds,
                aesWindowSeconds = config.aesWindowSeconds,
                decisionIntervalSeconds = config.decisionIntervalSeconds,
                aesMinimumDistanceMeters = config.aesMinimumDistanceMeters,
                aesMinimumUniqueRooms = config.aesMinimumUniqueRooms,
                aesMinimumHeadRotationDegrees = config.aesMinimumHeadRotationDegrees,
                proxyWindowSeconds = config.proxyWindowSeconds,
                proxyColdStartRatio = config.proxyColdStartRatio,
                proxyLowBoundary = config.proxyLowBoundary,
                proxyHighBoundary = config.proxyHighBoundary,
                minimumUtteranceGapSeconds = config.minimumUtteranceGapSeconds,
                aagPlaybackEnabled = config.aagPlaybackEnabled,
                aagTextModeEnabled = config.aagTextModeEnabled,
                captionVisibleSeconds = config.captionVisibleSeconds,
                stalestRecencyFloorSeconds = config.stalestRecencyFloorSeconds,
                stalestMeaningfulDwellSeconds = config.stalestMeaningfulDwellSeconds,
                initialSupportLevel = config.initialSupportLevel.ToString(),
                roomTieOrder = string.Join("|", (config.rooms ?? Array.Empty<ExperimentRoomMapping>())
                    .Where(room => room != null)
                    .OrderBy(room => room.tieOrder)
                    .Select(room => $"{room.roomId}:{room.tieOrder}")),
                configuredClipIds = string.Join("|", (config.aagClips ?? Array.Empty<ExperimentAagClip>())
                    .Where(binding => binding != null)
                    .Select(binding => binding.clipId ?? string.Empty)),
                configuredCaptions = string.Join("|", (config.aagClips ?? Array.Empty<ExperimentAagClip>())
                    .Where(binding => binding != null)
                    .Select(binding => $"{binding.clipId}={binding.captionText}")),
                configuredClips = (config.aagClips ?? Array.Empty<ExperimentAagClip>())
                    .Where(binding => binding != null)
                    .Select(binding => new ConfiguredClipLog
                    {
                        clipId = binding.clipId ?? string.Empty,
                        durationSeconds = binding.clip != null ? binding.clip.length : 0f,
                        hasAudioClip = binding.clip != null,
                    })
                    .ToArray(),
            }, "config_snapshot");

            Write(new SessionStartLog
            {
                t = 0f,
                sessionId = currentSessionId,
                participantId = participantId ?? string.Empty,
                setId = setId ?? string.Empty,
                guideMode = guideMode.ToString(),
                startedAtUtc = startedAtUtc,
            }, "session_start");
            FlushNow();
            Debug.Log($"[LoggingManager] log={currentLogPath}", this);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[LoggingManager] open failed: {exception.Message}", this);
            CloseSession();
            return false;
        }
    }

    public void Tick()
    {
        if (!WriterOpen || Time.realtimeSinceStartup < nextFlushTime) return;
        FlushNow();
        nextFlushTime = Time.realtimeSinceStartup + flushIntervalSeconds;
    }

    public void Write(object record, string recordType)
    {
        if (!WriterOpen || record == null) return;
        try
        {
            writer.WriteLine(JsonUtility.ToJson(record));
            writtenRowCount++;
            lastRecordType = recordType ?? string.Empty;
            lastWriteTime = DateTime.Now.ToString("HH:mm:ss.fff");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[LoggingManager] write failed type={recordType}: {exception.Message}", this);
        }
    }

    public void WriteSystem(string eventName, string detail)
    {
        Write(new SystemLog
        {
            t = SessionTime,
            eventName = eventName ?? string.Empty,
            detail = detail ?? string.Empty,
        }, "system");
    }

    public void FlushNow()
    {
        if (!WriterOpen) return;
        try { writer.Flush(); }
        catch (Exception exception) { Debug.LogError($"[LoggingManager] flush failed: {exception.Message}", this); }
    }

    public void CloseSession()
    {
        if (writer != null)
        {
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[LoggingManager] close failed: {exception.Message}", this);
            }
        }
        writer = null;
        writerOpen = false;
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused) FlushNow();
    }

    private void OnDestroy()
    {
        CloseSession();
    }

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "session";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}
