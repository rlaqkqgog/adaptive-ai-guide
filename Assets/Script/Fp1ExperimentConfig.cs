using System;
using System.Collections.Generic;
using UnityEngine;

public enum ExperimentGuideMode
{
    AAG,
    VG,
    NG,
}

public enum AagSupportLevel
{
    VeryEasy,
    Easy,
    Normal,
    Hard,
    VeryHard,
}

[Serializable]
public sealed class ExperimentRoomMapping
{
    public string roomUuid = string.Empty;
    public string roomId = string.Empty;
    public string displayName = string.Empty;
    public int tieOrder;
}

[Serializable]
public sealed class ExperimentAagClip
{
    public string clipId = string.Empty;
    [TextArea(2, 3)] public string captionText = string.Empty;
    public AudioClip clip;
}

[Serializable]
public sealed class ExperimentTowerAnchor
{
    [Tooltip("Stable identifier written to delivery logs.")]
    public string towerId = string.Empty;
    [Tooltip("Only stones with this manifest color are accepted by this tower.")]
    public string acceptedColor = string.Empty;
    [Tooltip("UUID of the spatial anchor saved once at this tower location. This value is shared by S1, S2 and S3.")]
    public string anchorUuid = string.Empty;
    [HideInInspector] public bool hasFallbackPose;
    [HideInInspector] public Vector3 fallbackWorldPosition;
    [HideInInspector] public Quaternion fallbackWorldRotation = Quaternion.identity;
    [Tooltip("Local offset of Stonepagoda.fbx from the localized spatial anchor.")]
    public Vector3 visualLocalPosition = Vector3.zero;
    public Vector3 visualLocalEulerAngles = Vector3.zero;
    public Vector3 visualLocalScale = Vector3.one * 0.1f;
    [Tooltip("World-space color/count label displayed above the pagoda.")]
    public Vector3 labelLocalPosition = new Vector3(0f, 2f, 0f);
    [Min(0.1f)] public float labelWidthMeters = 1.8f;
    [Min(0.1f)] public float labelHeightMeters = 0.5f;
    [Min(1)] public int requiredStoneCount = 3;
    [Tooltip("Local center of the box where a carried stone is accepted after release.")]
    public Vector3 deliveryZoneCenter = new Vector3(0f, 0.75f, 0f);
    public Vector3 deliveryZoneSize = new Vector3(1.5f, 1.5f, 1.5f);
    public bool requireGrabBeforeDelivery = true;
}

[CreateAssetMenu(fileName = "FP1ExperimentConfig", menuName = "AAG/FP1 Experiment Config")]
public sealed class Fp1ExperimentConfig : ScriptableObject
{
    [Header("Pilot choices")]
    public string[] participantIds = { "P01", "P02" };
    public string[] setIds = { "FP1-S1", "FP1-S2", "FP1-S3" };

    [Header("Session")]
    [Min(1f)] public float sessionTimeLimitSeconds = 1200f;
    [Min(0.02f)] public float trackIntervalSeconds = 0.1f;
    [Min(0.05f)] public float roomMinDwellSeconds = 0.5f;
    [Min(1f)] public float logFlushIntervalSeconds = 10f;

    [Header("Shared fixed stone pagodas")]
    [Tooltip("The same four spatial-anchor UUIDs are loaded for every S1/S2/S3 session.")]
    public bool fixedTowersEnabled = true;
    [Tooltip("Resources-relative wrapper prefab path. Resolves Assets/Resources/Prefabs/StonepagodaTower.prefab.")]
    public string fixedTowerResourcesPath = "Prefabs/StonepagodaTower";
    public bool randomizeTowerColorsPerSession = true;
    public ExperimentTowerAnchor[] fixedTowers =
    {
        new ExperimentTowerAnchor { towerId = "Tower-1", acceptedColor = "Red" },
        new ExperimentTowerAnchor { towerId = "Tower-2", acceptedColor = "Blue" },
        new ExperimentTowerAnchor { towerId = "Tower-3", acceptedColor = "Yellow" },
        new ExperimentTowerAnchor { towerId = "Tower-4", acceptedColor = "Green" },
    };

    [Header("AES")]
    [Min(1f)] public float aesWindowSeconds = 24f;
    [Min(0.5f)] public float decisionIntervalSeconds = 2f;
    [Tooltip("Passive boundary for the configured AES distance window. The loose gate blocks only when all three AES signals are below their boundaries.")]
    [Min(0f)] public float aesMinimumDistanceMeters = 12f;
    [Tooltip("Passive boundary for unique rooms. Two means a one-room session can still be classified as clearly passive when the other signals are also low.")]
    [Min(0)] public int aesMinimumUniqueRooms = 2;
    [Tooltip("Passive boundary for accumulated head rotation in the AES window.")]
    [Min(0f)] public float aesMinimumHeadRotationDegrees = 2800f;

    [Header("Empty-hand RevisitProxy")]
    [Min(1f)] public float proxyWindowSeconds = 12f;
    [Range(0f, 1f)] public float proxyColdStartRatio = 0.5f;
    [Range(0f, 1f)] public float proxyLowBoundary = 0.33f;
    [Range(0f, 1f)] public float proxyHighBoundary = 0.66f;

    [Header("AAG")]
    [Tooltip("Development-only output. Shows the configured utterance text through the same request path used by audio.")]
    public bool aagTextModeEnabled = true;
    [Tooltip("Production output. Keep text mode off during participant sessions when this is enabled.")]
    public bool aagPlaybackEnabled;
    [Min(0.1f)] public float captionVisibleSeconds = 3f;
    [Min(0f)] public float minimumUtteranceGapSeconds = 8f;
    [Tooltip("A visited room must remain out of the current path for this long before stalest selection can recommend it.")]
    [Min(0f)] public float stalestRecencyFloorSeconds = 60f;
    [Tooltip("Only visits at least this long update the stalest ordering timestamp. Lostness revisit measurement is unaffected.")]
    [Min(0f)] public float stalestMeaningfulDwellSeconds = 5f;
    public AagSupportLevel initialSupportLevel = AagSupportLevel.Normal;
    public ExperimentAagClip[] aagClips = Array.Empty<ExperimentAagClip>();

    [Header("FP1 rooms")]
    public ExperimentRoomMapping[] rooms = Array.Empty<ExperimentRoomMapping>();

    private Dictionary<string, ExperimentRoomMapping> roomByUuid;
    private Dictionary<string, ExperimentAagClip> aagClipById;

    public ExperimentRoomMapping FindRoom(string roomUuid)
    {
        EnsureLookups();
        return string.IsNullOrEmpty(roomUuid) || !roomByUuid.TryGetValue(roomUuid, out var mapping)
            ? null
            : mapping;
    }

    public ExperimentAagClip FindAagClip(string clipId)
    {
        EnsureLookups();
        return string.IsNullOrEmpty(clipId) || !aagClipById.TryGetValue(clipId, out var binding)
            ? null
            : binding;
    }

    public void RebuildLookups()
    {
        roomByUuid = new Dictionary<string, ExperimentRoomMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var room in rooms ?? Array.Empty<ExperimentRoomMapping>())
        {
            if (room == null || string.IsNullOrWhiteSpace(room.roomUuid)) continue;
            roomByUuid[room.roomUuid.Trim()] = room;
        }

        aagClipById = new Dictionary<string, ExperimentAagClip>(StringComparer.Ordinal);
        foreach (var binding in aagClips ?? Array.Empty<ExperimentAagClip>())
        {
            if (binding == null || string.IsNullOrWhiteSpace(binding.clipId)) continue;
            aagClipById[binding.clipId.Trim()] = binding;
        }
    }

    private void EnsureLookups()
    {
        if (roomByUuid == null || aagClipById == null) RebuildLookups();
    }

    private void OnValidate()
    {
        if (proxyHighBoundary < proxyLowBoundary) proxyHighBoundary = proxyLowBoundary;
        RebuildLookups();
    }
}
