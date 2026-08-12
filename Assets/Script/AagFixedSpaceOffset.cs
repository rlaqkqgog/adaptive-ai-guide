using System;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Applies one world-space translation to experiment-owned content. It never
/// moves OVRCameraRig, TrackingSpace, MRUK, MRUKRoom, or MRUKAnchor objects.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(8500)]
public sealed class AagFixedSpaceOffset : MonoBehaviour
{
    public const float MaximumAcceptedCorrectionMeters = 40f;

    [Header("Correction")]
    [SerializeField] private bool correctionEnabled;
    [SerializeField] private bool horizontalOnly = true;
    [SerializeField] private Vector3 correctionOffsetMeters;

    [Header("Optional scene-owned content roots")]
    [Tooltip("Runtime experiment roots are registered by ExperimentMain. Add only other app-owned content here.")]
    [SerializeField] private List<Transform> sceneContentRoots = new List<Transform>();
    [SerializeField] private bool applySceneRootsOnStart = true;

    private readonly Dictionary<Transform, Vector3> baselineWorldPositions =
        new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, string> contentIds =
        new Dictionary<Transform, string>();

    public bool CorrectionEnabled => correctionEnabled;
    public bool HorizontalOnly => horizontalOnly;
    public Vector3 CorrectionOffsetMeters => EffectiveOffset;
    public int RegisteredRootCount => baselineWorldPositions.Count(pair => pair.Key != null);

    private Vector3 EffectiveOffset
    {
        get
        {
            var value = correctionOffsetMeters;
            if (horizontalOnly) value.y = 0f;
            return value;
        }
    }

    private void Start()
    {
        if (!applySceneRootsOnStart) return;
        foreach (var root in sceneContentRoots.Where(value => value != null))
        {
            if (!TryRegisterContent(root, $"scene:{root.name}", out var failure))
                Debug.LogError($"[AAG Fixed Offset] Scene root rejected: {failure}", this);
        }
    }

    public void Configure(bool enabledValue, Vector3 offsetMeters, bool horizontalOnlyValue)
    {
        correctionEnabled = enabledValue;
        horizontalOnly = horizontalOnlyValue;
        correctionOffsetMeters = offsetMeters;
    }

    public void SetCorrectionOffset(Vector3 offsetMeters, bool enableCorrection, bool applyNow = true)
    {
        correctionOffsetMeters = offsetMeters;
        correctionEnabled = enableCorrection;
        if (applyNow) ApplyCorrection();
    }

    public bool TryRegisterContent(Transform contentRoot, string contentId, out string failure)
    {
        failure = string.Empty;
        if (contentRoot == null)
        {
            failure = "content_root_null";
            return false;
        }
        if (!IsSafeContentRoot(contentRoot, out failure)) return false;

        if (baselineWorldPositions.ContainsKey(contentRoot))
        {
            if (correctionEnabled) ApplyRoot(contentRoot);
            return true;
        }

        var registeredAncestor = baselineWorldPositions.Keys.FirstOrDefault(value =>
            value != null && contentRoot.IsChildOf(value));
        if (registeredAncestor != null)
        {
            // The ancestor already moves this transform; registering both would
            // double the correction.
            return true;
        }

        var registeredDescendants = baselineWorldPositions.Keys
            .Where(value => value != null && value.IsChildOf(contentRoot))
            .ToArray();
        if (registeredDescendants.Length > 0)
        {
            failure = $"content_root_contains_registered_descendants_{registeredDescendants.Length}";
            return false;
        }

        baselineWorldPositions.Add(contentRoot, contentRoot.position);
        contentIds[contentRoot] = string.IsNullOrWhiteSpace(contentId)
            ? contentRoot.name
            : contentId.Trim();
        if (correctionEnabled) ApplyRoot(contentRoot);
        return true;
    }

    /// <summary>Idempotently reapplies the configured correction to every registered root.</summary>
    public void ApplyCorrection()
    {
        RemoveDestroyedRoots();
        if (!correctionEnabled)
        {
            Debug.Log("[AAG Fixed Offset] Apply skipped because correction is disabled.", this);
            return;
        }

        foreach (var root in baselineWorldPositions.Keys.ToArray()) ApplyRoot(root);
        Debug.Log(
            $"[AAG Fixed Offset] Applied {EffectiveOffset:F4} to {RegisteredRootCount} content root(s).",
            this);
    }

    /// <summary>Returns registered roots to the poses captured before correction.</summary>
    public void ResetCorrection()
    {
        RemoveDestroyedRoots();
        foreach (var pair in baselineWorldPositions)
        {
            if (pair.Key != null) pair.Key.position = pair.Value;
        }
        Debug.Log($"[AAG Fixed Offset] Reset {RegisteredRootCount} content root(s).", this);
    }

    /// <summary>
    /// Drops runtime registrations without moving objects. ExperimentMain calls
    /// this while destroying session-owned content.
    /// </summary>
    public void ClearRegisteredContent()
    {
        baselineWorldPositions.Clear();
        contentIds.Clear();
    }

    public bool TryGetBaseline(Transform root, out Vector3 baseline) =>
        baselineWorldPositions.TryGetValue(root, out baseline);

    private void ApplyRoot(Transform root)
    {
        if (root == null || !baselineWorldPositions.TryGetValue(root, out var baseline)) return;
        root.position = baseline + EffectiveOffset;
        var id = contentIds.TryGetValue(root, out var value) ? value : root.name;
        Debug.Log(
            $"[AAG Fixed Offset] content={id}; baseline={baseline:F4}; "
            + $"offset={EffectiveOffset:F4}; corrected={root.position:F4}",
            this);
    }

    private static bool IsSafeContentRoot(Transform root, out string failure)
    {
        failure = string.Empty;
        var cameraRig = FindFirstObjectByType<OVRCameraRig>();
        if (cameraRig != null
            && (root == cameraRig.transform
                || root.IsChildOf(cameraRig.transform)
                || cameraRig.transform.IsChildOf(root)))
        {
            failure = "refuses_camera_rig_tracking_space_or_related_transform";
            return false;
        }

        var mruk = MRUK.Instance != null ? MRUK.Instance : FindFirstObjectByType<MRUK>();
        if (mruk != null
            && (root == mruk.transform
                || root.IsChildOf(mruk.transform)
                || mruk.transform.IsChildOf(root)))
        {
            failure = "refuses_mruk_or_related_transform";
            return false;
        }

        if (root.GetComponent<MRUKRoom>() != null || root.GetComponent<MRUKAnchor>() != null)
        {
            failure = "refuses_mruk_room_or_anchor";
            return false;
        }
        return true;
    }

    private void RemoveDestroyedRoots()
    {
        foreach (var root in baselineWorldPositions.Keys.Where(value => value == null).ToArray())
        {
            baselineWorldPositions.Remove(root);
            contentIds.Remove(root);
        }
    }
}
