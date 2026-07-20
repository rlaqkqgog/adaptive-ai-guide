using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
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
    private IHand leftSelectingHand;
    private int leftSelectingPointerId;
    private float inventoryPinchHoldStartedAt = -1f;
    private bool inventoryPinchHoldAnnounced;
    private bool inventoryPinchHoldCompleted;

    private const float InventoryPinchHoldSeconds = 1.2f;
    private const float MaximumLeftIndexDistanceMeters = 0.45f;

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
        ResetInventoryPinchHold(true, "object_disabled");
        Unsubscribe();
        selectingPointers.Clear();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Update()
    {
        UpdateInventoryPinchHold();
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
                var firstSelection = selectingPointers.Add(pointerEvent.Identifier)
                    && selectingPointers.Count == 1;
                var leftHand = ResolveLeftSelectingHand(pointerEvent.Identifier, out var resolution);
                ResolveOwner()?.NotifyInventoryHandResolution(
                    this, pointerEvent.Identifier, leftHand != null, resolution);
                if (leftHand != null)
                    BeginInventoryPinchHold(pointerEvent.Identifier, leftHand);
                if (firstSelection)
                {
                    hasBeenGrabbed = true;
                    NotifyGrabbed();
                }
                break;

            case PointerEventType.Unselect:
            case PointerEventType.Cancel:
                if (pointerEvent.Identifier == leftSelectingPointerId)
                    ResetInventoryPinchHold(true, pointerEvent.Type == PointerEventType.Cancel
                        ? "selection_cancelled"
                        : "selection_released");
                if (selectingPointers.Remove(pointerEvent.Identifier) && selectingPointers.Count == 0)
                    NotifyDropped();
                break;
        }
    }

    private IHand ResolveLeftSelectingHand(int pointerIdentifier, out string resolution)
    {
        // PointerEvent.Data has no stable type contract in Interaction SDK.
        // Resolve the originating interactor from the event's guaranteed ID.
        object interactor = null;
        if (UniqueIdentifier.TryGetInstanceFromIdentifier(
                Context.Global.GetInstance(), pointerIdentifier, out interactor))
        {
            IHand interactorHand = null;
            if (interactor is HandGrabInteractor nearHandGrab)
                interactorHand = nearHandGrab.Hand;
            else if (interactor is DistanceHandGrabInteractor distanceHandGrab)
                interactorHand = distanceHandGrab.Hand;

            if (interactorHand != null && interactorHand.Handedness == Handedness.Left)
            {
                resolution = $"pointer_id:{interactor.GetType().Name}";
                return interactorHand;
            }
        }

        // Some prefab forwarding paths originate from a generic GrabInteractor,
        // even when a tracked hand performed the pinch. In that case, require an
        // actively pinching left index tip to be physically close to this stone.
        IHand closestHand = null;
        var closestDistance = float.PositiveInfinity;
        foreach (var behaviour in FindObjectsByType<MonoBehaviour>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!(behaviour is IHand candidate)
                || candidate.Handedness != Handedness.Left
                || !candidate.IsConnected
                || !candidate.GetIndexFingerIsPinching())
                continue;

            Pose handPose;
            if (!candidate.GetJointPose(HandJointId.HandIndexTip, out handPose)
                && !candidate.GetPointerPose(out handPose))
                continue;
            var distance = Vector3.Distance(handPose.position, transform.position);
            if (distance >= closestDistance) continue;
            closestDistance = distance;
            closestHand = candidate;
        }

        if (closestHand != null && closestDistance <= MaximumLeftIndexDistanceMeters)
        {
            resolution = $"left_index_proximity:{closestDistance:F3}m; "
                + $"pointerType={interactor?.GetType().Name ?? "unresolved"}";
            return closestHand;
        }

        resolution = $"unresolved; pointerType={interactor?.GetType().Name ?? "not_found"}; "
            + $"nearestLeftIndex={(float.IsPositiveInfinity(closestDistance) ? "none" : $"{closestDistance:F3}m")}";
        return null;
    }

    private void BeginInventoryPinchHold(int pointerIdentifier, IHand hand)
    {
        if (incidental || hand == null) return;
        if (leftSelectingHand != null && leftSelectingPointerId != pointerIdentifier)
            ResetInventoryPinchHold(true, "left_interactor_changed");
        leftSelectingHand = hand;
        leftSelectingPointerId = pointerIdentifier;
        inventoryPinchHoldStartedAt = -1f;
        inventoryPinchHoldAnnounced = false;
        inventoryPinchHoldCompleted = false;
    }

    private void UpdateInventoryPinchHold()
    {
        if (incidental
            || inventoryPinchHoldCompleted
            || leftSelectingHand == null
            || !selectingPointers.Contains(leftSelectingPointerId))
            return;

        var pinching = leftSelectingHand.IsConnected
            && leftSelectingHand.GetIndexFingerIsPinching();
        if (!pinching)
        {
            if (inventoryPinchHoldStartedAt >= 0f)
                ResetInventoryPinchTimer("pinch_released");
            return;
        }

        if (inventoryPinchHoldStartedAt < 0f)
        {
            inventoryPinchHoldStartedAt = Time.unscaledTime;
            inventoryPinchHoldAnnounced = true;
            ResolveOwner()?.NotifyInventoryStoreHoldStarted(this, InventoryPinchHoldSeconds);
        }

        var elapsed = Mathf.Max(0f, Time.unscaledTime - inventoryPinchHoldStartedAt);
        ResolveOwner()?.NotifyInventoryStoreHoldProgress(this, elapsed, InventoryPinchHoldSeconds);
        if (elapsed < InventoryPinchHoldSeconds) return;

        inventoryPinchHoldCompleted = true;
        ResolveOwner()?.NotifyInventoryStoreHoldCompleted(this, InventoryPinchHoldSeconds);
    }

    private void ResetInventoryPinchTimer(string reason)
    {
        if (inventoryPinchHoldAnnounced)
            ResolveOwner()?.NotifyInventoryStoreHoldCancelled(this, reason);
        inventoryPinchHoldStartedAt = -1f;
        inventoryPinchHoldAnnounced = false;
    }

    private void ResetInventoryPinchHold(bool notifyOwner, string reason)
    {
        if (notifyOwner && inventoryPinchHoldAnnounced && !inventoryPinchHoldCompleted)
            ResolveOwner()?.NotifyInventoryStoreHoldCancelled(this, reason);
        leftSelectingHand = null;
        leftSelectingPointerId = 0;
        inventoryPinchHoldStartedAt = -1f;
        inventoryPinchHoldAnnounced = false;
        inventoryPinchHoldCompleted = false;
    }
}
