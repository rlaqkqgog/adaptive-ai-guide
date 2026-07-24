using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Dedicated Quest UUID loader for incidental objects. It owns no stone or
/// tower state and falls back to a pose reconstructed from another localized
/// incidental anchor in the same set.
/// </summary>
[DisallowMultipleComponent]
public sealed class IncidentalAnchorLoader : MonoBehaviour
{
    private OVRSpatialAnchor anchorPrefab;
    private readonly Dictionary<Guid, OVRSpatialAnchor> realAnchors = new Dictionary<Guid, OVRSpatialAnchor>();
    private readonly Dictionary<Guid, GameObject> approximateRoots = new Dictionary<Guid, GameObject>();
    private readonly Dictionary<Guid, string> failures = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, string> approximateReasons = new Dictionary<Guid, string>();
    private int loadVersion;

    public bool IsLoading { get; private set; }
    public int LoadedCount => realAnchors.Count + approximateRoots.Count;
    public int RealLoadedCount => realAnchors.Count;
    public int ApproximateCount => approximateRoots.Count;
    public IReadOnlyDictionary<Guid, string> Failures => failures;
    public IReadOnlyDictionary<Guid, string> ApproximateReasons => approximateReasons;
    public string LastRecoverySummary { get; private set; } = string.Empty;

    public void Initialize(OVRSpatialAnchor prefab) => anchorPrefab = prefab;

    public void Load(
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries,
        AagRigidPoseRecovery.Solution? globalRecoverySolution = null,
        bool useGlobalSolutionOnly = false,
        string setId = "")
    {
        ClearLoadedAnchors();
        failures.Clear();
        LastRecoverySummary = string.Empty;
        var normalized = entries?
            .Where(pair => pair.Key != Guid.Empty && pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value)
            ?? new Dictionary<Guid, AagIncidentalAnchorEntry>();
        if (normalized.Count == 0) return;

        if (useGlobalSolutionOnly && globalRecoverySolution.HasValue)
        {
            foreach (var uuid in normalized.Keys)
                failures[uuid] = "deterministic_room_constellation";
            IsLoading = true;
            if (!TrySpawnS3RoomLocalRoots(normalized, setId))
                SpawnRigidApproximateRoots(normalized, globalRecoverySolution.Value, setId);
            IsLoading = false;
            Debug.Log(
                $"[Incidental Load] deterministic room-constellation mode "
                + $"set={setId}; ready={LoadedCount}/{normalized.Count}", this);
            return;
        }
        IsLoading = true;
        var version = ++loadVersion;
        LoadAsync(normalized, version, globalRecoverySolution, setId);
    }

    public bool TryGetTransform(Guid uuid, out Transform result)
    {
        if (realAnchors.TryGetValue(uuid, out var anchor) && anchor != null)
        {
            result = anchor.transform;
            return true;
        }
        if (approximateRoots.TryGetValue(uuid, out var root) && root != null)
        {
            result = root.transform;
            return true;
        }
        result = null;
        return false;
    }

    public int FreezeRealAnchorPoses()
    {
        var frozen = 0;
        foreach (var anchor in realAnchors.Values)
        {
            if (anchor == null || !anchor.enabled) continue;
            anchor.enabled = false;
            frozen++;
        }
        return frozen;
    }

    public void ClearLoadedAnchors()
    {
        loadVersion++;
        IsLoading = false;
        foreach (var anchor in realAnchors.Values)
        {
            if (anchor == null) continue;
            anchor.gameObject.SetActive(false);
            Destroy(anchor.gameObject);
        }
        foreach (var root in approximateRoots.Values)
            if (root != null) Destroy(root);
        realAnchors.Clear();
        approximateRoots.Clear();
        failures.Clear();
        approximateReasons.Clear();
        LastRecoverySummary = string.Empty;
    }

    private async void LoadAsync(
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries,
        int version,
        AagRigidPoseRecovery.Solution? globalRecoverySolution,
        string setId)
    {
        try
        {
            var requested = entries.Keys.ToArray();
            var returned = new List<OVRSpatialAnchor.UnboundAnchor>();
            var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(requested, returned);
            if (version != loadVersion) return;
            if (!result.Success)
            {
                foreach (var uuid in requested) failures[uuid] = $"load failed: {result.Status}";
            }
            else
            {
                var returnedUuids = new HashSet<Guid>(returned.Select(value => value.Uuid));
                foreach (var uuid in requested)
                    if (!returnedUuids.Contains(uuid)) failures[uuid] = "not returned";

                foreach (var unbound in returned)
                {
                    if (version != loadVersion) return;
                    var uuid = unbound.Uuid;
                    var localized = unbound.Localized || await unbound.LocalizeAsync();
                    if (version != loadVersion) return;
                    if (!localized)
                    {
                        failures[uuid] = "localize failed";
                        continue;
                    }
                    if (!unbound.TryGetPose(out var pose))
                    {
                        failures[uuid] = "pose unavailable";
                        continue;
                    }
                    if (anchorPrefab == null)
                    {
                        failures[uuid] = "anchor shell prefab missing";
                        continue;
                    }

                    var anchor = Instantiate(anchorPrefab, pose.position, pose.rotation);
                    anchor.gameObject.name = $"IncidentalAnchor {entries[uuid].object_id}";
                    unbound.BindTo(anchor);
                    realAnchors[uuid] = anchor;
                    failures.Remove(uuid);
                }
            }
        }
        catch (Exception exception)
        {
            if (version != loadVersion) return;
            foreach (var uuid in entries.Keys.Where(uuid => !realAnchors.ContainsKey(uuid)))
                failures[uuid] = $"exception: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            if (version == loadVersion)
            {
                if (globalRecoverySolution.HasValue)
                    SpawnRigidApproximateRoots(entries, globalRecoverySolution.Value, setId);
                else
                    SpawnLegacyApproximateRoots(entries);
                IsLoading = false;
                Debug.Log($"[Incidental Load] complete real={RealLoadedCount} approx={ApproximateCount} total={LoadedCount}", this);
            }
        }
    }

    private void SpawnLegacyApproximateRoots(IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries)
    {
        var references = entries
            .Where(pair => realAnchors.TryGetValue(pair.Key, out var anchor) && anchor != null)
            .Select(pair => new { Entry = pair.Value, Transform = realAnchors[pair.Key].transform })
            .ToArray();

        foreach (var pair in entries)
        {
            if (realAnchors.ContainsKey(pair.Key) || !failures.TryGetValue(pair.Key, out var reason)) continue;
            var entry = pair.Value;
            var position = entry.FallbackPosition;
            var rotation = entry.FallbackRotation;
            var referenceId = "raw_fallback";
            if (references.Length > 0)
            {
                var reference = references
                    .OrderBy(value => (value.Entry.FallbackPosition - entry.FallbackPosition).sqrMagnitude)
                    .First();
                var deltaRotation = reference.Transform.rotation * Quaternion.Inverse(reference.Entry.FallbackRotation);
                position = reference.Transform.position
                    + deltaRotation * (entry.FallbackPosition - reference.Entry.FallbackPosition);
                rotation = deltaRotation * entry.FallbackRotation;
                referenceId = reference.Entry.object_id;
            }

            var root = new GameObject($"IncidentalApproxAnchor {entry.object_id}");
            root.transform.SetPositionAndRotation(position, rotation);
            approximateRoots[pair.Key] = root;
            approximateReasons[pair.Key] = reason;
            failures.Remove(pair.Key);
            Debug.Log($"[Incidental Load] approximate object={entry.object_id} reference={referenceId} reason={reason}", this);
        }
    }

    private bool TrySpawnS3RoomLocalRoots(
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries,
        string setId)
    {
        if (!string.Equals(setId, AagExperimentSpaceCatalog.Fp1S3, StringComparison.Ordinal)
            || entries.Count != AagS3IncidentalRoomLocalCatalog.Count)
            return false;

        var placements = new List<(Guid uuid, AagIncidentalAnchorEntry entry, Vector3 position, Quaternion rotation)>();
        foreach (var pair in entries.OrderBy(value => value.Value.object_id, StringComparer.Ordinal))
        {
            if (!AagS3IncidentalRoomLocalCatalog.TryResolve(
                    pair.Value.object_id, out var position, out var rotation, out var resolveFailure))
            {
                LastRecoverySummary =
                    $"set={setId}; status=failed; source=room_local_20260721; "
                    + $"object={pair.Value.object_id}; reason={resolveFailure}";
                failures[pair.Key] = $"{failures[pair.Key]}; room_local={resolveFailure}";
                return false;
            }
            if (!AagRecoveryPlacementValidator.TryValidate(position, out _, out var placementFailure))
            {
                LastRecoverySummary =
                    $"set={setId}; status=failed; source=room_local_20260721; "
                    + $"object={pair.Value.object_id}; reason={placementFailure}";
                failures[pair.Key] = $"{failures[pair.Key]}; room_local={placementFailure}";
                return false;
            }
            placements.Add((pair.Key, pair.Value, position, rotation));
        }

        foreach (var placement in placements)
        {
            var root = new GameObject($"IncidentalRoomLocalAnchor {placement.entry.object_id}");
            root.transform.SetPositionAndRotation(placement.position, placement.rotation);
            approximateRoots[placement.uuid] = root;
            approximateReasons[placement.uuid] = "deterministic_room_local_20260721";
            failures.Remove(placement.uuid);
            Debug.Log(
                $"[Incidental Load] room-local object={placement.entry.object_id} "
                + $"position={placement.position}", this);
        }
        LastRecoverySummary =
            $"set={setId}; status=success; source=room_local_20260721; recovered={placements.Count}";
        return true;
    }

    private void SpawnRigidApproximateRoots(
        IReadOnlyDictionary<Guid, AagIncidentalAnchorEntry> entries,
        AagRigidPoseRecovery.Solution stoneSolution,
        string setId)
    {
        var missing = entries
            .Where(pair => !realAnchors.ContainsKey(pair.Key) && failures.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Value.object_id, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
        {
            LastRecoverySummary = $"set={setId}; missing=0; source=none";
            return;
        }

        var references = entries
            .Where(pair => realAnchors.TryGetValue(pair.Key, out var anchor) && anchor != null)
            .Select(pair => new AagRigidPoseRecovery.Reference(
                pair.Value.object_id,
                pair.Value.FallbackPosition,
                realAnchors[pair.Key].transform.position))
            .ToArray();

        var solution = stoneSolution;
        var source = references.Length == 0 ? "room_constellation" : "stone_consensus";
        var compatible = references.Length == 0 || references.All(reference =>
            Vector3.Distance(solution.TransformPoint(reference.CapturedPosition), reference.CurrentPosition)
                <= AagRigidPoseRecovery.InlierToleranceMeters);
        if (!compatible)
        {
            if (AagRigidPoseRecovery.TrySolve(references, out solution, out var solveFailure))
            {
                source = "incidental_consensus";
            }
            else if (references.Length == 2
                && AagRigidPoseRecovery.TrySolveTwoReference(references, out solution, out var pairFailure))
            {
                source = "incidental_pair";
            }
            else
            {
                var finalFailure = solveFailure;
                if (references.Length == 2)
                {
                    AagRigidPoseRecovery.TrySolveTwoReference(
                        references, out _, out var twoReferenceFailure);
                    finalFailure = twoReferenceFailure;
                }
                LastRecoverySummary =
                    $"set={setId}; status=failed; stone_solution_incompatible=true; "
                    + $"incidentalReferences={references.Length}; reason={finalFailure}";
                foreach (var pair in missing)
                    failures[pair.Key] = $"{failures[pair.Key]}; rigid_recovery={finalFailure}";
                return;
            }
        }

        var placements = new List<(Guid uuid, AagIncidentalAnchorEntry entry, Vector3 position, Quaternion rotation, string rooms, string reason)>();
        foreach (var pair in missing)
        {
            var position = solution.TransformPoint(pair.Value.FallbackPosition);
            var rotation = solution.TransformRotation(pair.Value.FallbackRotation);
            if (!AagRigidPoseRecovery.IsFinite(position) || !AagRigidPoseRecovery.IsFinite(rotation))
            {
                LastRecoverySummary = $"set={setId}; status=failed; object={pair.Value.object_id}; reason=non_finite_pose";
                failures[pair.Key] = $"{failures[pair.Key]}; rigid_recovery=non_finite_pose";
                return;
            }
            if (!AagRecoveryPlacementValidator.TryValidate(position, out var rooms, out var placementFailure))
            {
                LastRecoverySummary =
                    $"set={setId}; status=failed; object={pair.Value.object_id}; reason={placementFailure}";
                failures[pair.Key] = $"{failures[pair.Key]}; rigid_recovery={placementFailure}";
                return;
            }
            placements.Add((pair.Key, pair.Value, position, rotation, rooms, failures[pair.Key]));
        }

        foreach (var placement in placements)
        {
            var root = new GameObject($"IncidentalRigidApproxAnchor {placement.entry.object_id}");
            root.transform.SetPositionAndRotation(placement.position, placement.rotation);
            approximateRoots[placement.uuid] = root;
            approximateReasons[placement.uuid] = $"{placement.reason}; rigid_source={source}";
            failures.Remove(placement.uuid);
            Debug.Log(
                $"[Incidental Load] rigid approximate object={placement.entry.object_id} "
                + $"source={source} rooms={placement.rooms} reason={placement.reason}",
                this);
        }

        LastRecoverySummary =
            $"set={setId}; status=success; source={source}; references={references.Length}; "
            + $"recovered={placements.Count}; inliers={string.Join("|", solution.InlierIds ?? Array.Empty<string>())}; "
            + $"rms={solution.RmsResidualMeters:F4}; maxResidual={solution.MaxResidualMeters:F4}";
    }
}
