using System;
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

[Serializable]
public sealed class BehaviorMetricsSnapshot
{
    public float aesDistanceMeters;
    public int aesUniqueRooms;
    public float aesHeadRotationDegrees;
    public int aesRecentFoundTargets;
    public float aesCombinedScore;
    public bool aesClearlyPassive;
    public bool aesGatePassed;
    public bool proxyHasValue;
    public float proxyRevisitSeconds;
    public float proxyEmptyHandSeconds;
    public float consecutiveEmptyHandSeconds;
    public float proxyRatio;
    public bool proxyColdStart;
}

/// <summary>
/// Always-on measurement owner for all guide conditions. Raw tracking continues
/// while carrying; only the empty-hand revisit clock is frozen.
/// </summary>
[DisallowMultipleComponent]
public sealed class BehaviorMetrics : MonoBehaviour
{
    private sealed class MetricSample
    {
        public float time;
        public float distance;
        public float headRotation;
        public string roomUuid;
    }

    [Serializable]
    private sealed class TrackLog
    {
        public string type = "track";
        public float t;
        public float x;
        public float y;
        public float z;
        public float yaw;
        public float pitch;
        public int carrying;
        public string carriedObjectId;
        public string roomUuid;
        public string roomId;
    }

    [Serializable]
    private sealed class RoomEventLog
    {
        public string type;
        public float t;
        public string roomUuid;
        public string roomId;
        public int carrying;
        public bool revisitAtEntry;
        public int visitCount;
        public float dwellSeconds;
    }

    [Serializable]
    private sealed class AesLog
    {
        public string type = "aes";
        public float t;
        public float windowSeconds;
        public float distanceMeters;
        public int uniqueRooms;
        public float headRotationDegrees;
        public int recentFoundTargets;
        public float combinedScore;
        public bool clearlyPassive;
        public bool gatePassed;
        public string guideMode;
    }

    [Serializable]
    private sealed class ProxyLog
    {
        public string type = "revisit_proxy";
        public float t;
        public float windowSeconds;
        public float totalAccumulatedEmptyHandSeconds;
        public float revisitEmptyHandSeconds;
        public float windowEmptyHandSeconds;
        public float consecutiveEmptyHandSeconds;
        public float proxyRatio;
        public bool hasValue;
        public bool coldStart;
        public int carrying;
        public bool windowFrozen;
        public int totalFoundTargets;
        public float lastTargetFoundAt;
        public string guideMode;
    }

    [Header("Scene References")]
    [SerializeField] private ExperimentConfig config;
    [SerializeField] private Transform headTransform;
    [SerializeField] private LoggingManager loggingManager;

    [Header("Live State (Play Mode)")]
    [SerializeField] private string currentRoomUuid = string.Empty;
    [SerializeField] private string currentRoomId = string.Empty;
    [SerializeField] private bool carrying;
    [SerializeField] private string carriedObjectId = string.Empty;
    [SerializeField] private bool windowFrozen;

    [Header("AES (Play Mode)")]
    [SerializeField] private float windowDistanceMeters;
    [SerializeField] private int windowUniqueRooms;
    [SerializeField] private float windowHeadRotationDegrees;
    [SerializeField] private float aesValue;
    [SerializeField] private bool clearlyPassive;
    [SerializeField] private bool aesGatePassed;
    [SerializeField] private int windowFoundTargets;

    [Header("Lostness Proxy (Play Mode)")]
    [SerializeField] private int totalRoomVisits;
    [SerializeField] private int uniqueRoomVisits;
    [SerializeField] private int revisitCount;
    [SerializeField] private float currentLostness;
    [SerializeField] private string lastVisitedRoom = string.Empty;
    [SerializeField] private int totalFoundTargets;
    [SerializeField] private float lastTargetFoundAt = -1f;

    private readonly List<MetricSample> metricSamples = new List<MetricSample>();
    private readonly HashSet<string> visitedRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastVisitedAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> lastMeaningfulVisitAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> stalestEligibleAfter = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> roomVisitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly EmptyHandRevisitWindow revisitWindow = new EmptyHandRevisitWindow();
    private readonly HashSet<string> foundTargetIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<float> targetFoundTimes = new List<float>();

    private bool sessionActive;
    private bool hasPreviousPose;
    private Vector3 previousPosition;
    private float previousYaw;
    private float previousPitch;
    private bool currentRoomIsRevisit;
    private string candidateRoomUuid = string.Empty;
    private float candidateRoomDwell;
    private float currentRoomEnteredAt;
    private float consecutiveEmptyHandSeconds;
    private Func<string> physicalRoomProvider;

    public int Carrying => carrying ? 1 : 0;
    public string CarriedObjectId => carriedObjectId;
    public string CurrentRoomUuid => currentRoomUuid;
    public string CurrentRoomId => currentRoomId;
    public Vector3 LatestHeadPosition { get; private set; }

    /// <summary>
    /// Overrides only the participant head's current-room identity. Arbitrary
    /// object-position queries continue to use MRUK geometry through
    /// <see cref="ResolveRoomUuid"/>.
    /// </summary>
    public void SetPhysicalRoomProvider(Func<string> provider)
    {
        physicalRoomProvider = provider;
    }

    public void BeginSession(ExperimentConfig sessionConfig, Transform trackedHead, LoggingManager logger)
    {
        config = sessionConfig;
        headTransform = trackedHead;
        loggingManager = logger;
        ResetSession();
        sessionActive = true;
    }

    public void ResetSession()
    {
        sessionActive = false;
        metricSamples.Clear();
        revisitWindow.Reset();
        foundTargetIds.Clear();
        targetFoundTimes.Clear();
        visitedRooms.Clear();
        lastVisitedAt.Clear();
        lastMeaningfulVisitAt.Clear();
        stalestEligibleAfter.Clear();
        roomVisitCounts.Clear();
        currentRoomUuid = string.Empty;
        currentRoomId = string.Empty;
        currentRoomIsRevisit = false;
        candidateRoomUuid = string.Empty;
        candidateRoomDwell = 0f;
        currentRoomEnteredAt = 0f;
        carrying = false;
        carriedObjectId = string.Empty;
        windowFrozen = false;
        hasPreviousPose = false;
        previousPosition = Vector3.zero;
        previousYaw = 0f;
        previousPitch = 0f;
        LatestHeadPosition = Vector3.zero;
        windowDistanceMeters = 0f;
        windowUniqueRooms = 0;
        windowHeadRotationDegrees = 0f;
        aesValue = 0f;
        clearlyPassive = false;
        aesGatePassed = false;
        windowFoundTargets = 0;
        totalRoomVisits = 0;
        uniqueRoomVisits = 0;
        revisitCount = 0;
        currentLostness = 0f;
        lastVisitedRoom = string.Empty;
        totalFoundTargets = 0;
        lastTargetFoundAt = -1f;
        consecutiveEmptyHandSeconds = 0f;
    }

    public void Sample(float sampleDelta, ExperimentGuideMode guideMode)
    {
        if (!sessionActive || config == null || loggingManager == null) return;
        if (headTransform == null)
        {
            loggingManager.WriteSystem("tracking_lost", "headTransform_missing");
            return;
        }

        var position = headTransform.position;
        LatestHeadPosition = position;
        var euler = headTransform.eulerAngles;
        var yaw = NormalizeAngle(euler.y);
        var pitch = NormalizeAngle(euler.x);
        var physicalRoomUuid = physicalRoomProvider?.Invoke();
        UpdateStableRoom(
            string.IsNullOrWhiteSpace(physicalRoomUuid)
                ? ResolveObservedRoomUuid(position)
                : physicalRoomUuid,
            sampleDelta);

        var distance = 0f;
        var rotation = 0f;
        if (hasPreviousPose)
        {
            var delta = position - previousPosition;
            distance = new Vector2(delta.x, delta.z).magnitude;
            rotation = Mathf.Abs(Mathf.DeltaAngle(previousYaw, yaw))
                + Mathf.Abs(Mathf.DeltaAngle(previousPitch, pitch));
        }
        previousPosition = position;
        previousYaw = yaw;
        previousPitch = pitch;
        hasPreviousPose = true;

        metricSamples.Add(new MetricSample
        {
            time = loggingManager.SessionTime,
            distance = distance,
            headRotation = rotation,
            roomUuid = currentRoomUuid,
        });
        TrimMetricWindow();

        windowFrozen = carrying;
        consecutiveEmptyHandSeconds = carrying
            ? 0f
            : consecutiveEmptyHandSeconds + Mathf.Max(0f, sampleDelta);
        revisitWindow.Sample(sampleDelta, !carrying, currentRoomIsRevisit);

        loggingManager.Write(new TrackLog
        {
            t = loggingManager.SessionTime,
            x = position.x,
            y = position.y,
            z = position.z,
            yaw = yaw,
            pitch = pitch,
            carrying = Carrying,
            carriedObjectId = carriedObjectId,
            roomUuid = currentRoomUuid,
            roomId = currentRoomId,
        }, "track");
    }

    public BehaviorMetricsSnapshot Evaluate(ExperimentGuideMode guideMode)
    {
        var snapshot = new BehaviorMetricsSnapshot();
        TrimFoundTargetWindow();
        var rooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in metricSamples)
        {
            snapshot.aesDistanceMeters += sample.distance;
            snapshot.aesHeadRotationDegrees += sample.headRotation;
            if (!string.IsNullOrEmpty(sample.roomUuid)) rooms.Add(sample.roomUuid);
        }
        snapshot.aesUniqueRooms = rooms.Count;
        snapshot.aesRecentFoundTargets = targetFoundTimes.Count;
        snapshot.aesCombinedScore = snapshot.aesDistanceMeters
            + snapshot.aesUniqueRooms
            + snapshot.aesHeadRotationDegrees / 180f;
        // Loose pilot gate: block only when every exploration signal is below
        // its threshold. Any clear evidence of exploration is enough to pass.
        snapshot.aesClearlyPassive = config != null
            && snapshot.aesDistanceMeters < config.aesMinimumDistanceMeters
            && snapshot.aesUniqueRooms < config.aesMinimumUniqueRooms
            && snapshot.aesHeadRotationDegrees < config.aesMinimumHeadRotationDegrees
            && snapshot.aesRecentFoundTargets == 0;
        snapshot.aesGatePassed = !snapshot.aesClearlyPassive;

        revisitWindow.GetWindow(config.proxyWindowSeconds, out var revisitSeconds, out var emptyHandSeconds);
        var coldThreshold = config.proxyWindowSeconds * config.proxyColdStartRatio;
        snapshot.proxyHasValue = revisitWindow.TotalAccumulatedEmptyHandSeconds >= coldThreshold
            && emptyHandSeconds > 0f;
        snapshot.proxyRevisitSeconds = revisitSeconds;
        snapshot.proxyEmptyHandSeconds = emptyHandSeconds;
        snapshot.consecutiveEmptyHandSeconds = consecutiveEmptyHandSeconds;
        snapshot.proxyRatio = emptyHandSeconds > 0f ? revisitSeconds / emptyHandSeconds : 0f;
        snapshot.proxyColdStart = !snapshot.proxyHasValue;

        windowDistanceMeters = snapshot.aesDistanceMeters;
        windowUniqueRooms = snapshot.aesUniqueRooms;
        windowHeadRotationDegrees = snapshot.aesHeadRotationDegrees;
        windowFoundTargets = snapshot.aesRecentFoundTargets;
        aesValue = snapshot.aesCombinedScore;
        clearlyPassive = snapshot.aesClearlyPassive;
        aesGatePassed = snapshot.aesGatePassed;
        currentLostness = snapshot.proxyRatio;

        loggingManager?.Write(new AesLog
        {
            t = loggingManager.SessionTime,
            windowSeconds = config.aesWindowSeconds,
            distanceMeters = snapshot.aesDistanceMeters,
            uniqueRooms = snapshot.aesUniqueRooms,
            headRotationDegrees = snapshot.aesHeadRotationDegrees,
            recentFoundTargets = snapshot.aesRecentFoundTargets,
            combinedScore = snapshot.aesCombinedScore,
            clearlyPassive = snapshot.aesClearlyPassive,
            gatePassed = snapshot.aesGatePassed,
            guideMode = guideMode.ToString(),
        }, "aes");
        loggingManager?.Write(new ProxyLog
        {
            t = loggingManager.SessionTime,
            windowSeconds = config.proxyWindowSeconds,
            totalAccumulatedEmptyHandSeconds = revisitWindow.TotalAccumulatedEmptyHandSeconds,
            revisitEmptyHandSeconds = snapshot.proxyRevisitSeconds,
            windowEmptyHandSeconds = snapshot.proxyEmptyHandSeconds,
            consecutiveEmptyHandSeconds = snapshot.consecutiveEmptyHandSeconds,
            proxyRatio = snapshot.proxyRatio,
            hasValue = snapshot.proxyHasValue,
            coldStart = snapshot.proxyColdStart,
            carrying = Carrying,
            windowFrozen = windowFrozen,
            totalFoundTargets = totalFoundTargets,
            lastTargetFoundAt = lastTargetFoundAt,
            guideMode = guideMode.ToString(),
        }, "revisit_proxy");

        return snapshot;
    }

    public void EndSession()
    {
        if (!sessionActive) return;
        if (!string.IsNullOrEmpty(currentRoomUuid) && loggingManager != null && loggingManager.WriterOpen)
        {
            var now = loggingManager.SessionTime;
            loggingManager.Write(new RoomEventLog
            {
                type = "room_exit",
                t = now,
                roomUuid = currentRoomUuid,
                roomId = currentRoomId,
                carrying = Carrying,
                revisitAtEntry = currentRoomIsRevisit,
                visitCount = roomVisitCounts.TryGetValue(currentRoomUuid, out var count) ? count : 0,
                dwellSeconds = Mathf.Max(0f, now - currentRoomEnteredAt),
            }, "room_exit");
        }
        sessionActive = false;
    }

    public bool TryBeginCarry(string objectId, out string rejectionReason)
    {
        rejectionReason = string.Empty;
        objectId ??= string.Empty;
        if (carrying && !string.Equals(carriedObjectId, objectId, StringComparison.Ordinal))
        {
            rejectionReason = $"already_carrying_{carriedObjectId}";
            return false;
        }
        carrying = true;
        consecutiveEmptyHandSeconds = 0f;
        carriedObjectId = objectId;
        windowFrozen = true;
        return true;
    }

    public void EndCarry(string objectId)
    {
        if (!string.Equals(carriedObjectId, objectId ?? string.Empty, StringComparison.Ordinal)) return;
        carrying = false;
        consecutiveEmptyHandSeconds = 0f;
        carriedObjectId = string.Empty;
        windowFrozen = false;
    }

    public void ForceClearCarry()
    {
        carrying = false;
        consecutiveEmptyHandSeconds = 0f;
        carriedObjectId = string.Empty;
        windowFrozen = false;
    }

    /// <summary>
    /// Records direct evidence of successful search. A newly found target keeps
    /// the loose AES gate open for one AES window and starts a fresh Lostness
    /// episode so pre-find revisit time cannot penalize post-find behavior.
    /// </summary>
    public bool NotifyTargetFound(string objectId, string source)
    {
        objectId ??= string.Empty;
        if (!sessionActive || string.IsNullOrWhiteSpace(objectId) || !foundTargetIds.Add(objectId))
            return false;

        var now = loggingManager != null ? loggingManager.SessionTime : 0f;
        targetFoundTimes.Add(now);
        totalFoundTargets = foundTargetIds.Count;
        lastTargetFoundAt = now;
        revisitWindow.Reset();
        currentRoomIsRevisit = false;
        currentLostness = 0f;
        var aesEvidenceSeconds = config != null ? config.aesWindowSeconds : 0f;
        loggingManager?.WriteSystem(
            "target_found_metrics_reset",
            $"object={objectId}; source={source ?? string.Empty}; totalFound={totalFoundTargets}; "
            + $"aesActiveEvidenceSeconds={aesEvidenceSeconds:F1}; lostnessWindowReset=true");
        return true;
    }

    public bool HasVisited(string roomUuid)
    {
        return !string.IsNullOrEmpty(roomUuid) && visitedRooms.Contains(roomUuid);
    }

    public float GetLastVisitedAt(string roomUuid)
    {
        return !string.IsNullOrEmpty(roomUuid) && lastVisitedAt.TryGetValue(roomUuid, out var time)
            ? time
            : float.NegativeInfinity;
    }

    public float GetLastMeaningfulVisitAt(string roomUuid)
    {
        return !string.IsNullOrEmpty(roomUuid) && lastMeaningfulVisitAt.TryGetValue(roomUuid, out var time)
            ? time
            : float.NegativeInfinity;
    }

    public float GetStalestEligibleAfter(string roomUuid)
    {
        return !string.IsNullOrEmpty(roomUuid) && stalestEligibleAfter.TryGetValue(roomUuid, out var time)
            ? time
            : 0f;
    }

    public string ResolveRoomUuid(Vector3 worldPosition)
    {
        if (MRUK.Instance == null) return string.Empty;
        MRUKRoom containing = null;
        foreach (var room in MRUK.Instance.Rooms)
        {
            var inside = false;
            foreach (var floor in room.FloorAnchors)
            {
                if (floor == null || floor.PlaneBoundary2D == null || floor.PlaneBoundary2D.Count < 3) continue;
                var local = floor.transform.InverseTransformPoint(worldPosition);
                if (!floor.IsPositionInBoundary(new Vector2(local.x, local.y))) continue;
                inside = true;
                break;
            }
            if (!inside) continue;
            if (containing != null) return string.Empty;
            containing = room;
        }
        if (containing == null || containing.Anchor == null || containing.Anchor.Uuid == Guid.Empty) return string.Empty;
        return containing.Anchor.Uuid.ToString();
    }

    /// <summary>
    /// Resolves a physical HMD or already-aligned content position against the
    /// unchanged MRUK floor geometry. Raw MRUK-generated positions must continue
    /// to use <see cref="ResolveRoomUuid"/> directly.
    /// </summary>
    public string ResolveObservedRoomUuid(Vector3 observedWorldPosition)
    {
        var canonicalPosition = AagMrukSpaceCorrection.ObservedToMrukPosition(
            observedWorldPosition);
        if (ExperimentSpaceRuntime.UsesBakedReferenceSpace && AagMrukSpaceCorrection.IsApplied)
            return AagFp2BakedSpace.TryResolveRoom(canonicalPosition, out var bakedRoomUuid)
                ? bakedRoomUuid.ToString()
                : string.Empty;
        return ResolveRoomUuid(canonicalPosition);
    }

    private void UpdateStableRoom(string rawRoomUuid, float sampleDelta)
    {
        rawRoomUuid ??= string.Empty;
        if (string.Equals(rawRoomUuid, currentRoomUuid, StringComparison.OrdinalIgnoreCase))
        {
            candidateRoomUuid = string.Empty;
            candidateRoomDwell = 0f;
            return;
        }
        if (!string.Equals(rawRoomUuid, candidateRoomUuid, StringComparison.OrdinalIgnoreCase))
        {
            candidateRoomUuid = rawRoomUuid;
            candidateRoomDwell = sampleDelta;
            return;
        }

        candidateRoomDwell += sampleDelta;
        if (candidateRoomDwell < config.roomMinDwellSeconds) return;
        var now = loggingManager.SessionTime;

        if (!string.IsNullOrEmpty(currentRoomUuid))
        {
            var dwellSeconds = Mathf.Max(0f, now - currentRoomEnteredAt);
            RecordStalestExit(currentRoomUuid, now, dwellSeconds);
            loggingManager.Write(new RoomEventLog
            {
                type = "room_exit",
                t = now,
                roomUuid = currentRoomUuid,
                roomId = currentRoomId,
                carrying = Carrying,
                revisitAtEntry = currentRoomIsRevisit,
                visitCount = roomVisitCounts.TryGetValue(currentRoomUuid, out var count) ? count : 0,
                dwellSeconds = dwellSeconds,
            }, "room_exit");
        }

        currentRoomUuid = candidateRoomUuid;
        var mapping = config.FindRoom(currentRoomUuid);
        currentRoomId = mapping != null ? mapping.roomId : RoomFallbackId(currentRoomUuid);
        currentRoomIsRevisit = !string.IsNullOrEmpty(currentRoomUuid) && visitedRooms.Contains(currentRoomUuid);
        currentRoomEnteredAt = now;
        if (!string.IsNullOrEmpty(currentRoomUuid))
        {
            if (currentRoomIsRevisit) revisitCount++;
            totalRoomVisits++;
            visitedRooms.Add(currentRoomUuid);
            uniqueRoomVisits = visitedRooms.Count;
            roomVisitCounts[currentRoomUuid] = roomVisitCounts.TryGetValue(currentRoomUuid, out var count) ? count + 1 : 1;
            lastVisitedAt[currentRoomUuid] = now;
            lastVisitedRoom = currentRoomId;
            loggingManager.Write(new RoomEventLog
            {
                type = "room_enter",
                t = now,
                roomUuid = currentRoomUuid,
                roomId = currentRoomId,
                carrying = Carrying,
                revisitAtEntry = currentRoomIsRevisit,
                visitCount = roomVisitCounts[currentRoomUuid],
                dwellSeconds = 0f,
            }, "room_enter");
        }
        candidateRoomUuid = string.Empty;
        candidateRoomDwell = 0f;
    }

    private void RecordStalestExit(string roomUuid, float now, float dwellSeconds)
    {
        if (string.IsNullOrEmpty(roomUuid) || config == null) return;
        stalestEligibleAfter[roomUuid] = now + Mathf.Max(0f, config.stalestRecencyFloorSeconds);
        if (dwellSeconds >= Mathf.Max(0f, config.stalestMeaningfulDwellSeconds))
            lastMeaningfulVisitAt[roomUuid] = now;
    }

    private void TrimMetricWindow()
    {
        var minimumTime = loggingManager.SessionTime - config.aesWindowSeconds;
        while (metricSamples.Count > 0 && metricSamples[0].time < minimumTime) metricSamples.RemoveAt(0);
    }

    private void TrimFoundTargetWindow()
    {
        if (config == null || loggingManager == null) return;
        var minimumTime = loggingManager.SessionTime - config.aesWindowSeconds;
        while (targetFoundTimes.Count > 0 && targetFoundTimes[0] < minimumTime)
            targetFoundTimes.RemoveAt(0);
    }

    private static float NormalizeAngle(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
    }

    private static string RoomFallbackId(string roomUuid)
    {
        return string.IsNullOrEmpty(roomUuid) ? string.Empty : roomUuid.Substring(0, Mathf.Min(8, roomUuid.Length));
    }
}
