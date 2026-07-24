using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Accepts only a released stone whose manifest color matches this tower.
/// Wrong-color attempts are logged once per trigger entry and never increment the label.
/// </summary>
[DisallowMultipleComponent]
public sealed class TowerDeliveryZone : MonoBehaviour
{
    private ExperimentMain owner;
    private string towerId = string.Empty;
    private string acceptedColor = string.Empty;
    private bool requireGrabBeforeDelivery = true;
    private TowerColorLabel colorLabel;
    private readonly HashSet<string> deliveredObjectIds = new HashSet<string>();
    private readonly HashSet<string> rejectedObjectIds = new HashSet<string>();

    public void Initialize(
        ExperimentMain experimentMain,
        string id,
        string color,
        bool requireGrab,
        TowerColorLabel label)
    {
        owner = experimentMain;
        towerId = id ?? string.Empty;
        acceptedColor = color ?? string.Empty;
        requireGrabBeforeDelivery = requireGrab;
        colorLabel = label;
    }

    private void OnTriggerEnter(Collider other)
    {
        TryDeliver(other);
    }

    private void OnTriggerStay(Collider other)
    {
        TryDeliver(other);
    }

    private void OnTriggerExit(Collider other)
    {
        var experimentObject = FindExperimentObject(other);
        if (experimentObject != null) rejectedObjectIds.Remove(experimentObject.ObjectId);
    }

    private void TryDeliver(Collider other)
    {
        if (owner == null || other == null) return;
        var experimentObject = FindExperimentObject(other);
        if (experimentObject == null || experimentObject.IsIncidental || experimentObject.IsGrabbed) return;
        if (requireGrabBeforeDelivery && !experimentObject.HasBeenGrabbed) return;
        if (string.IsNullOrEmpty(experimentObject.ObjectId)
            || deliveredObjectIds.Contains(experimentObject.ObjectId)) return;

        if (!string.Equals(experimentObject.ObjectColor, acceptedColor, StringComparison.OrdinalIgnoreCase))
        {
            if (rejectedObjectIds.Add(experimentObject.ObjectId))
                owner.NotifyWrongTower(experimentObject, towerId);
            return;
        }

        if (owner.TryNotifyObjectDelivered(experimentObject, towerId))
        {
            deliveredObjectIds.Add(experimentObject.ObjectId);
            rejectedObjectIds.Remove(experimentObject.ObjectId);
            colorLabel?.RecordDelivery();
        }
    }

    private static ExperimentObject FindExperimentObject(Collider other)
    {
        return other != null ? other.GetComponentInParent<ExperimentObject>() : null;
    }
}
