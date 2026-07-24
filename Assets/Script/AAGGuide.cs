using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public sealed class AagGuideTarget
{
    public string objectId;
    public string roomUuid;
    public string roomId;
    public bool delivered;
}

/// <summary>
/// AAG-only decision and speech output. Measurement remains in BehaviorMetrics;
/// every decision, including suppressions, is written through LoggingManager.
/// </summary>
[DisallowMultipleComponent]
public sealed class AAGGuide : MonoBehaviour
{
    private sealed class RoomChoice
    {
        public string roomUuid;
        public string roomId;
        public int remainingCount;
        public bool visited;
        public float lastVisitedAt;
        public float lastMeaningfulVisitAt;
        public float eligibleAfter;
        public bool currentRoom;
        public int tieOrder;
        public string selectionRule;
    }

    [Serializable]
    private sealed class DecisionLog
    {
        public string type = "decision";
        public float t;
        public float aesDistanceMeters;
        public int aesUniqueRooms;
        public float aesHeadRotationDegrees;
        public int aesRecentFoundTargets;
        public float aesCombinedScore;
        public bool aesClearlyPassive;
        public bool aesGatePassed;
        public bool proxyHasValue;
        public float proxyRatio;
        public float proxyRevisitSeconds;
        public float proxyEmptyHandSeconds;
        public int carrying;
        public bool playbackEnabled;
        public bool textModeEnabled;
        public string lostnessZone;
        public int zoneVoteUnderload;
        public int zoneVoteOptimal;
        public int zoneVoteOverload;
        public string zoneVoteResult;
        public string previousLevel;
        public string proposedLevel;
        public int levelAdjustment;
        public string level;
        public string effectiveLevel;
        public string candidateRooms;
        public string currentRoomId;
        public string roomUuid;
        public string roomId;
        public string selectionRule;
        public int selectedRoomRemainingObjects;
        public bool selectedRoomVisited;
        public float selectedRoomLastVisitedAt;
        public float selectedRoomLastMeaningfulVisitAt;
        public float selectedRoomEligibleAfter;
        public string clipId;
        public string wouldPlayClipId;
        public string playedClipId;
        public bool played;
        public string outputMode;
        public string reason;
    }

    [Header("Scene References")]
    [SerializeField] private Fp1ExperimentConfig config;
    [SerializeField] private BehaviorMetrics behaviorMetrics;
    [SerializeField] private LoggingManager loggingManager;
    [SerializeField] private AAGUtterancePlayer utterancePlayer;

    [Header("Last Decision (Play Mode)")]
    [SerializeField] private bool playbackEnabled;
    [SerializeField] private bool textModeEnabled;
    [SerializeField] private bool gatePassed;
    [SerializeField] private string currentLostnessZone = string.Empty;
    [SerializeField] private string selectedLevel = string.Empty;
    [SerializeField] private string selectedRoom = string.Empty;
    [SerializeField] private string selectionReason = string.Empty;
    [SerializeField] private bool spoken;
    [SerializeField] private string outputMode = "none";
    [SerializeField] private string clipId = string.Empty;
    [SerializeField] private string suppressionReason = string.Empty;

    private float lastUtteranceTime = float.NegativeInfinity;
    private float lastAdaptationTime = float.NegativeInfinity;
    private AagSupportLevel currentSupportLevel = AagSupportLevel.Normal;
    private readonly Queue<string> recentZoneVotes = new Queue<string>(4);

    public void BeginSession(
        Fp1ExperimentConfig sessionConfig,
        BehaviorMetrics metrics,
        LoggingManager logger)
    {
        config = sessionConfig;
        behaviorMetrics = metrics;
        loggingManager = logger;
        if (utterancePlayer != null) utterancePlayer.BeginSession(sessionConfig, logger);
        StopAndReset();
    }

    public void StopAndReset(string interruptionReason = "guide_reset")
    {
        if (utterancePlayer != null) utterancePlayer.StopAndReset(interruptionReason);
        lastUtteranceTime = float.NegativeInfinity;
        lastAdaptationTime = float.NegativeInfinity;
        recentZoneVotes.Clear();
        currentSupportLevel = config != null ? config.initialSupportLevel : AagSupportLevel.Normal;
        gatePassed = false;
        playbackEnabled = config != null && config.aagPlaybackEnabled;
        textModeEnabled = config != null && config.aagTextModeEnabled;
        currentLostnessZone = string.Empty;
        selectedLevel = string.Empty;
        selectedRoom = string.Empty;
        selectionReason = string.Empty;
        spoken = false;
        outputMode = "none";
        clipId = string.Empty;
        suppressionReason = string.Empty;
    }

    public void NotifyTargetFound()
    {
        recentZoneVotes.Clear();
        currentLostnessZone = "cold_start";
    }

    public void Evaluate(BehaviorMetricsSnapshot snapshot, IReadOnlyCollection<AagGuideTarget> targets)
    {
        if (snapshot == null || config == null || behaviorMetrics == null || loggingManager == null) return;

        var zone = ResolveLostnessZone(snapshot);
        UpdateZoneVotes(zone);
        var hasVoteWindow = ResolveZoneVote(
            out var voteAdjustment,
            out var voteResult,
            out var underloadVotes,
            out var optimalVotes,
            out var overloadVotes);

        var previousLevel = currentSupportLevel;
        var choices = BuildRoomChoices(targets);
        var hasRemainingTargets = HasRemainingTargets(targets);
        var choice = snapshot.aesGatePassed ? ChooseRoom(choices, loggingManager.SessionTime) : null;
        var selectionRule = choice?.selectionRule
            ?? (!snapshot.aesGatePassed
                ? "none"
                : !hasRemainingTargets ? "no_remaining_targets" : "recent_floor_fallback");
        var outputEnabled = config.aagTextModeEnabled || config.aagPlaybackEnabled;
        var outputDue = loggingManager.SessionTime - lastUtteranceTime >= config.minimumUtteranceGapSeconds;
        var adaptationDue = loggingManager.SessionTime - lastAdaptationTime >= config.minimumUtteranceGapSeconds;
        var canProposeAdjustment = behaviorMetrics.Carrying == 0
            && outputEnabled
            && adaptationDue
            && hasVoteWindow
            && (!snapshot.aesGatePassed || outputDue);
        var proposedLevel = canProposeAdjustment
            ? ApplyLevelAdjustment(previousLevel, voteAdjustment)
            : previousLevel;
        var effectiveLevel = proposedLevel;
        var nextClipId = !snapshot.aesGatePassed
            ? "GF-01"
            : BuildClipId(proposedLevel, choice, out effectiveLevel);
        var decision = new DecisionLog
        {
            t = loggingManager.SessionTime,
            aesDistanceMeters = snapshot.aesDistanceMeters,
            aesUniqueRooms = snapshot.aesUniqueRooms,
            aesHeadRotationDegrees = snapshot.aesHeadRotationDegrees,
            aesRecentFoundTargets = snapshot.aesRecentFoundTargets,
            aesCombinedScore = snapshot.aesCombinedScore,
            aesClearlyPassive = snapshot.aesClearlyPassive,
            aesGatePassed = snapshot.aesGatePassed,
            proxyHasValue = snapshot.proxyHasValue,
            proxyRatio = snapshot.proxyRatio,
            proxyRevisitSeconds = snapshot.proxyRevisitSeconds,
            proxyEmptyHandSeconds = snapshot.proxyEmptyHandSeconds,
            carrying = behaviorMetrics.Carrying,
            playbackEnabled = config.aagPlaybackEnabled,
            textModeEnabled = config.aagTextModeEnabled,
            lostnessZone = zone,
            zoneVoteUnderload = underloadVotes,
            zoneVoteOptimal = optimalVotes,
            zoneVoteOverload = overloadVotes,
            zoneVoteResult = voteResult,
            previousLevel = previousLevel.ToString(),
            proposedLevel = proposedLevel.ToString(),
            levelAdjustment = 0,
            level = previousLevel.ToString(),
            effectiveLevel = snapshot.aesGatePassed ? effectiveLevel.ToString() : "GateFailure",
            candidateRooms = string.Join("|", choices.Select(value =>
                $"{value.roomId}:{value.remainingCount}:{(value.visited ? "visited" : "unvisited")}:"
                + $"{(value.currentRoom ? "current" : "other")}:"
                + $"{(loggingManager.SessionTime >= value.eligibleAfter ? "ready" : "recent")}")),
            currentRoomId = behaviorMetrics.CurrentRoomId,
            roomUuid = choice?.roomUuid ?? string.Empty,
            roomId = choice?.roomId ?? string.Empty,
            selectionRule = selectionRule,
            selectedRoomRemainingObjects = choice?.remainingCount ?? 0,
            selectedRoomVisited = choice?.visited ?? false,
            selectedRoomLastVisitedAt = choice == null || float.IsNegativeInfinity(choice.lastVisitedAt)
                ? -1f
                : choice.lastVisitedAt,
            selectedRoomLastMeaningfulVisitAt = choice == null || float.IsNegativeInfinity(choice.lastMeaningfulVisitAt)
                ? -1f
                : choice.lastMeaningfulVisitAt,
            selectedRoomEligibleAfter = choice?.eligibleAfter ?? -1f,
            clipId = nextClipId,
            wouldPlayClipId = nextClipId,
            playedClipId = string.Empty,
            outputMode = "none",
        };

        // A pocketed stone has already been found even though it has not yet
        // been delivered. Once every target is found, no search/lostness prompt
        // is valid (including the room-independent Normal and gate-failure clips).
        if (!hasRemainingTargets)
        {
            FinishSuppressed(decision, "no_remaining_targets");
            return;
        }
        if (behaviorMetrics.Carrying != 0)
        {
            FinishSuppressed(decision, "carrying");
            return;
        }
        if (!outputEnabled)
        {
            FinishSuppressed(decision, "decision_only");
            return;
        }
        if (loggingManager.SessionTime < config.aesWindowSeconds)
        {
            FinishSuppressed(decision, "aes_cold_start");
            return;
        }
        if (!outputDue)
        {
            FinishSuppressed(decision, "minimum_gap");
            return;
        }
        if (utterancePlayer == null)
        {
            FinishSuppressed(decision, "utterance_player_missing");
            return;
        }

        // During a blocked gate, keep the support state synchronized with the
        // recent zone vote even though the participant receives only GF output.
        if (!snapshot.aesGatePassed && canProposeAdjustment)
        {
            currentSupportLevel = proposedLevel;
            lastAdaptationTime = loggingManager.SessionTime;
            decision.levelAdjustment = (int)currentSupportLevel - (int)previousLevel;
            decision.level = currentSupportLevel.ToString();
        }

        if (!utterancePlayer.TryPlay(nextClipId, effectiveLevel, out var emittedMode, out var outputReason))
        {
            decision.outputMode = emittedMode;
            FinishSuppressed(decision, outputReason);
            return;
        }

        if (snapshot.aesGatePassed && canProposeAdjustment)
        {
            currentSupportLevel = proposedLevel;
            lastAdaptationTime = loggingManager.SessionTime;
            decision.levelAdjustment = (int)currentSupportLevel - (int)previousLevel;
            decision.level = currentSupportLevel.ToString();
        }
        lastUtteranceTime = loggingManager.SessionTime;
        decision.played = true;
        decision.playedClipId = nextClipId;
        decision.outputMode = emittedMode;
        decision.reason = outputReason;
        PublishDecision(decision);
    }

    private static bool HasRemainingTargets(IReadOnlyCollection<AagGuideTarget> targets)
    {
        return targets != null && targets.Any(target => target != null && !target.delivered);
    }

    private List<RoomChoice> BuildRoomChoices(IReadOnlyCollection<AagGuideTarget> targets)
    {
        return (targets ?? Array.Empty<AagGuideTarget>())
            .Where(target => target != null && !target.delivered && !string.IsNullOrEmpty(target.roomUuid))
            .GroupBy(target => target.roomUuid, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var mapping = config.FindRoom(group.Key);
                var visited = behaviorMetrics.HasVisited(group.Key);
                return new RoomChoice
                {
                    roomUuid = group.Key,
                    roomId = mapping != null ? mapping.roomId : first.roomId,
                    remainingCount = group.Count(),
                    visited = visited,
                    lastVisitedAt = behaviorMetrics.GetLastVisitedAt(group.Key),
                    lastMeaningfulVisitAt = behaviorMetrics.GetLastMeaningfulVisitAt(group.Key),
                    eligibleAfter = behaviorMetrics.GetStalestEligibleAfter(group.Key),
                    currentRoom = string.Equals(
                        group.Key,
                        behaviorMetrics.CurrentRoomUuid,
                        StringComparison.OrdinalIgnoreCase),
                    tieOrder = mapping?.tieOrder ?? int.MaxValue,
                };
            })
            .ToList();
    }

    private static RoomChoice ChooseRoom(IEnumerable<RoomChoice> choices, float now)
    {
        var unvisited = choices
            .Where(choice => !choice.visited && !choice.currentRoom)
            .OrderByDescending(choice => choice.remainingCount)
            .ThenBy(choice => choice.tieOrder)
            .FirstOrDefault();
        if (unvisited != null)
        {
            unvisited.selectionRule = "unvisited_first";
            return unvisited;
        }

        var stalest = choices
            .Where(choice => choice.visited && !choice.currentRoom && now >= choice.eligibleAfter)
            .OrderBy(choice => choice.lastMeaningfulVisitAt)
            .ThenByDescending(choice => choice.remainingCount)
            .ThenBy(choice => choice.tieOrder)
            .FirstOrDefault();
        if (stalest != null) stalest.selectionRule = "stalest_visited";
        return stalest;
    }

    private void UpdateZoneVotes(string lostnessZone)
    {
        if (behaviorMetrics.Carrying != 0)
        {
            recentZoneVotes.Clear();
            return;
        }
        if (lostnessZone != "underload" && lostnessZone != "optimal" && lostnessZone != "overload") return;
        recentZoneVotes.Enqueue(lostnessZone);
        while (recentZoneVotes.Count > 4) recentZoneVotes.Dequeue();
    }

    private bool ResolveZoneVote(
        out int adjustment,
        out string result,
        out int underloadVotes,
        out int optimalVotes,
        out int overloadVotes)
    {
        underloadVotes = recentZoneVotes.Count(value => value == "underload");
        optimalVotes = recentZoneVotes.Count(value => value == "optimal");
        overloadVotes = recentZoneVotes.Count(value => value == "overload");
        adjustment = 0;
        if (recentZoneVotes.Count < 4)
        {
            result = "insufficient";
            return false;
        }
        if (underloadVotes >= 3)
        {
            adjustment = 1;
            result = "underload";
            return true;
        }
        if (overloadVotes >= 3)
        {
            adjustment = -1;
            result = "overload";
            return true;
        }
        if (optimalVotes >= 3)
        {
            result = "optimal";
            return true;
        }
        result = "mixed_hold";
        return true;
    }

    private static AagSupportLevel ApplyLevelAdjustment(AagSupportLevel level, int adjustment)
    {
        return (AagSupportLevel)Mathf.Clamp(
            (int)level + adjustment,
            (int)AagSupportLevel.VeryEasy,
            (int)AagSupportLevel.VeryHard);
    }

    private string ResolveLostnessZone(BehaviorMetricsSnapshot snapshot)
    {
        if (!snapshot.proxyHasValue) return "cold_start";
        if (snapshot.proxyRatio < config.proxyLowBoundary) return "underload";
        if (snapshot.proxyRatio < config.proxyHighBoundary) return "optimal";
        return "overload";
    }

    private static string BuildClipId(
        AagSupportLevel level,
        RoomChoice choice,
        out AagSupportLevel effectiveLevel)
    {
        effectiveLevel = level;
        return level switch
        {
            AagSupportLevel.VeryEasy when choice != null && !choice.visited => $"VE-U-{choice.roomId}",
            AagSupportLevel.VeryEasy when choice != null => $"VE-V-{choice.roomId}",
            AagSupportLevel.VeryEasy => FallbackToNormal(out effectiveLevel),
            AagSupportLevel.Easy when choice != null => $"E-{choice.roomId}",
            AagSupportLevel.Easy => FallbackToNormal(out effectiveLevel),
            AagSupportLevel.Normal => "N-01",
            AagSupportLevel.Hard when choice != null && !choice.visited => "H-01",
            AagSupportLevel.Hard => FallbackToNormal(out effectiveLevel),
            _ => "VH-01",
        };
    }

    private static string FallbackToNormal(out AagSupportLevel effectiveLevel)
    {
        effectiveLevel = AagSupportLevel.Normal;
        return "N-01";
    }

    private void FinishSuppressed(DecisionLog decision, string reason)
    {
        decision.played = false;
        decision.reason = reason;
        PublishDecision(decision);
    }

    private void PublishDecision(DecisionLog decision)
    {
        gatePassed = decision.aesGatePassed;
        playbackEnabled = decision.playbackEnabled;
        textModeEnabled = decision.textModeEnabled;
        currentLostnessZone = decision.lostnessZone;
        selectedLevel = decision.level;
        selectedRoom = decision.roomId;
        selectionReason = decision.selectionRule;
        spoken = decision.played;
        outputMode = decision.outputMode;
        clipId = decision.clipId;
        suppressionReason = decision.played ? string.Empty : decision.reason;
        loggingManager.Write(decision, "decision");
    }
}
