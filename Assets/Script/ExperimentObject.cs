using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Thin event bridge for a spawned target. Existing grab/drop UnityEvents can call
/// these public methods without knowing anything about logging or session state.
/// </summary>
[DisallowMultipleComponent]
public sealed class ExperimentObject : MonoBehaviour
{
    [SerializeField] private string objectId = string.Empty;
    [SerializeField] private string objectColor = string.Empty;
    [SerializeField] private bool incidental;

    private ExperimentMain owner;
    private Grabbable grabbable;
    private readonly HashSet<int> selectingPointers = new HashSet<int>();
    private bool subscribed;
    private bool hasBeenGrabbed;

    public string ObjectId => objectId;
    public string ObjectColor => objectColor;
    public bool IsIncidental => incidental;
    public bool IsGrabbed => selectingPointers.Count > 0;
    public bool HasBeenGrabbed => hasBeenGrabbed;

    public void Initialize(ExperimentMain experimentMain, string id, bool isIncidental, string color = "")
    {
        owner = experimentMain;
        objectId = id ?? string.Empty;
        objectColor = color ?? string.Empty;
        incidental = isIncidental;
        hasBeenGrabbed = false;
        EnsureGrabSetup();
    }

    private void OnEnable()
    {
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
        selectingPointers.Clear();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    public void NotifyGrabbed()
    {
        if (!incidental) ResolveOwner()?.NotifyObjectGrabbed(this);
    }

    public void NotifyDropped()
    {
        if (!incidental) ResolveOwner()?.NotifyObjectDropped(this);
    }

    public void NotifyDelivered()
    {
        NotifyDeliveredTo(string.Empty);
    }

    public void NotifyDeliveredTo(string towerId)
    {
        if (!incidental) ResolveOwner()?.NotifyObjectDelivered(this, towerId ?? string.Empty);
    }

    private ExperimentMain ResolveOwner()
    {
        if (owner == null) owner = FindFirstObjectByType<ExperimentMain>();
        return owner;
    }

    /// <summary>
    /// Connects to the Grabbable already stored in the anchor target prefab.
    /// The prefab owns the grab setup; this component only forwards its signals.
    /// </summary>
    private void EnsureGrabSetup()
    {
        grabbable = GetComponent<Grabbable>();
        if (grabbable == null)
        {
            Debug.LogWarning($"[ExperimentObject] Prefab Grabbable missing for {objectId}; grab signal disabled.", this);
            return;
        }
        Subscribe();
    }

    private void Subscribe()
    {
        if (subscribed || grabbable == null) return;
        grabbable.WhenPointerEventRaised += HandlePointerEvent;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed || grabbable == null) return;
        grabbable.WhenPointerEventRaised -= HandlePointerEvent;
        subscribed = false;
    }

    private void HandlePointerEvent(PointerEvent pointerEvent)
    {
        switch (pointerEvent.Type)
        {
            case PointerEventType.Select:
                if (selectingPointers.Add(pointerEvent.Identifier) && selectingPointers.Count == 1)
                {
                    hasBeenGrabbed = true;
                    NotifyGrabbed();
                }
                break;

            case PointerEventType.Unselect:
            case PointerEventType.Cancel:
                if (selectingPointers.Remove(pointerEvent.Identifier) && selectingPointers.Count == 0)
                    NotifyDropped();
                break;
        }
    }
}
