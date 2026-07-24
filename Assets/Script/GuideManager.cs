using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Read-only bridge for guide logic. This component never creates, saves, or moves placement poses.
/// </summary>
[DisallowMultipleComponent]
public sealed class GuideManager : MonoBehaviour
{
    [SerializeField] private PlacementSetManager placementSetManager;

    public string CurrentSet => placementSetManager != null ? placementSetManager.CurrentSet : string.Empty;
    public IReadOnlyList<PlacementSetManager.LocalizedMarkerPose> LocalizedMarkerPoses =>
        placementSetManager != null ? placementSetManager.LocalizedMarkerPoses : System.Array.Empty<PlacementSetManager.LocalizedMarkerPose>();

    private void Awake()
    {
        if (placementSetManager == null) placementSetManager = GetComponent<PlacementSetManager>();
    }
}
