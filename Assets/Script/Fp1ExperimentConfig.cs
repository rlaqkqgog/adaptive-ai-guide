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
    public AudioClip clip;
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

    [Header("AES")]
    [Min(1f)] public float aesWindowSeconds = 60f;
    [Min(0.5f)] public float decisionIntervalSeconds = 5f;
    [Min(0f)] public float aesMinimumDistanceMeters;
    [Min(0)] public int aesMinimumUniqueRooms;
    [Min(0f)] public float aesMinimumHeadRotationDegrees;

    [Header("Empty-hand RevisitProxy")]
    [Min(1f)] public float proxyWindowSeconds = 60f;
    [Range(0f, 1f)] public float proxyColdStartRatio = 0.5f;
    [Range(0f, 1f)] public float proxyLowBoundary = 0.33f;
    [Range(0f, 1f)] public float proxyHighBoundary = 0.66f;

    [Header("AAG")]
    [Min(0f)] public float minimumUtteranceGapSeconds = 20f;
    public AagSupportLevel coldStartLevel = AagSupportLevel.Normal;
    public AagSupportLevel underloadLevel = AagSupportLevel.VeryHard;
    public AagSupportLevel optimalLevel = AagSupportLevel.Normal;
    public AagSupportLevel overloadLevel = AagSupportLevel.VeryEasy;
    public ExperimentAagClip[] aagClips = Array.Empty<ExperimentAagClip>();

    [Header("FP1 rooms")]
    public ExperimentRoomMapping[] rooms = Array.Empty<ExperimentRoomMapping>();

    private Dictionary<string, ExperimentRoomMapping> roomByUuid;
    private Dictionary<string, AudioClip> clipById;

    public ExperimentRoomMapping FindRoom(string roomUuid)
    {
        EnsureLookups();
        return string.IsNullOrEmpty(roomUuid) || !roomByUuid.TryGetValue(roomUuid, out var mapping)
            ? null
            : mapping;
    }

    public AudioClip FindClip(string clipId)
    {
        EnsureLookups();
        return string.IsNullOrEmpty(clipId) || !clipById.TryGetValue(clipId, out var clip) ? null : clip;
    }

    public void RebuildLookups()
    {
        roomByUuid = new Dictionary<string, ExperimentRoomMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var room in rooms ?? Array.Empty<ExperimentRoomMapping>())
        {
            if (room == null || string.IsNullOrWhiteSpace(room.roomUuid)) continue;
            roomByUuid[room.roomUuid.Trim()] = room;
        }

        clipById = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        foreach (var binding in aagClips ?? Array.Empty<ExperimentAagClip>())
        {
            if (binding == null || string.IsNullOrWhiteSpace(binding.clipId) || binding.clip == null) continue;
            clipById[binding.clipId.Trim()] = binding.clip;
        }
    }

    private void EnsureLookups()
    {
        if (roomByUuid == null || clipById == null) RebuildLookups();
    }

    private void OnValidate()
    {
        if (proxyHighBoundary < proxyLowBoundary) proxyHighBoundary = proxyLowBoundary;
        RebuildLookups();
    }
}
