using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed partial class AagFp1PlacementAuthoring
{
    private const string AuthoringUiLayerName = "AAG Authoring UI";
    private const string PreviewMarkerLayerName = "AAG Preview Marker";
    private const float AuthoringRayLengthMeters = 5f;

    private Canvas nextSetFallbackCanvas;
    private Canvas nextCandidateFallbackCanvas;
    private CanvasGroup nextSetFallbackCanvasGroup;
    private CanvasGroup nextCandidateFallbackCanvasGroup;
    private Collider nextSetFallbackCollider;
    private Collider nextCandidateFallbackCollider;
    private GameObject nextSetFallbackRoot;
    private GameObject nextCandidateFallbackRoot;

    private bool uiInteractionLocked;
    private bool leftPinchReleasePending;
    private bool rightPinchReleasePending;
    private string activeUiOperation = "NONE";
    private int activeOperationRemovedCount;
    private int activeOperationSpawnedCount;
    private bool activeOperationSucceeded;
    private string activeOperationReason = "not-completed";

    private LineRenderer leftHandRayLine;
    private LineRenderer rightHandRayLine;
    private GameObject leftHandRayReticle;
    private GameObject rightHandRayReticle;

    private int AuthoringUiLayer
    {
        get
        {
            var layer = LayerMask.NameToLayer(AuthoringUiLayerName);
            return layer >= 0 ? layer : LayerMask.NameToLayer("UI");
        }
    }

    private int PreviewMarkerLayer
    {
        get
        {
            var layer = LayerMask.NameToLayer(PreviewMarkerLayerName);
            return layer >= 0 ? layer : LayerMask.NameToLayer("Ignore Raycast");
        }
    }

    private int AuthoringUiRayMask => AuthoringUiLayer >= 0 ? 1 << AuthoringUiLayer : 0;
    private bool WaitingForPinchRelease => leftPinchReleasePending || rightPinchReleasePending;

    private void ConfigureAuthoringUi(
        GameObject canvasRoot,
        Canvas canvas,
        Button button,
        RectTransform buttonRect,
        bool isNextCandidate)
    {
        if (canvasRoot == null || canvas == null || button == null || buttonRect == null)
        {
            return;
        }

        SetLayerRecursively(canvasRoot, AuthoringUiLayer);
        var canvasGroup = canvasRoot.GetComponent<CanvasGroup>() ?? canvasRoot.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 1f;
        canvasGroup.interactable = true;
        canvasGroup.blocksRaycasts = true;

        Canvas.ForceUpdateCanvases();
        var boxCollider = button.gameObject.GetComponent<BoxCollider>() ?? button.gameObject.AddComponent<BoxCollider>();
        var rect = buttonRect.rect;
        boxCollider.center = Vector3.zero;
        boxCollider.size = new Vector3(Mathf.Max(1f, rect.width), Mathf.Max(1f, rect.height), 2f);
        boxCollider.enabled = true;

        if (isNextCandidate)
        {
            nextCandidateFallbackRoot = canvasRoot;
            nextCandidateFallbackCanvas = canvas;
            nextCandidateFallbackCanvasGroup = canvasGroup;
            nextCandidateFallbackCollider = boxCollider;
        }
        else
        {
            nextSetFallbackRoot = canvasRoot;
            nextSetFallbackCanvas = canvas;
            nextSetFallbackCanvasGroup = canvasGroup;
            nextSetFallbackCollider = boxCollider;
        }
    }

    private void InitializeUiInteractionLifecycle()
    {
        SetUiInteractionEnabled(true);
        CreateHandRayVisuals();
    }

    private void UpdateUiInteractionLifecycle()
    {
        UpdatePinchReleaseState(leftTrackedHand, ref leftPinchReleasePending, "LeftHandTracking");
        UpdatePinchReleaseState(rightTrackedHand, ref rightPinchReleasePending, "RightHandTracking");
        UpdateHandRayVisual(leftTrackedHand, leftHandRayLine, leftHandRayReticle);
        UpdateHandRayVisual(rightTrackedHand, rightHandRayLine, rightHandRayReticle);
    }

    private void UpdatePinchReleaseState(OVRHand hand, ref bool releasePending, string handName)
    {
        if (!releasePending || IsIndexPinching(hand))
        {
            return;
        }

        releasePending = false;
        if (!leftPinchReleasePending && !rightPinchReleasePending)
        {
            // The release edge is a stronger duplicate-input barrier than the time window.
            // Once released, the next deliberate pinch must be accepted immediately.
            nextCandidateAllowedTime = Mathf.Min(nextCandidateAllowedTime, Time.unscaledTime);
        }
        Debug.Log($"[AAG UI Lifecycle] pinch-release-confirmed hand={handName}; nextInputReady=true");
    }

    private void MarkPinchConsumed(bool leftConsumed, bool rightConsumed)
    {
        leftPinchReleasePending |= leftConsumed;
        rightPinchReleasePending |= rightConsumed;
    }

    private void MarkCurrentPinchesConsumedForUiClick()
    {
        MarkPinchConsumed(IsIndexPinching(leftTrackedHand), IsIndexPinching(rightTrackedHand));
    }

    private bool CanAcceptAuthoringUiInput()
    {
        return isReady
            && !isGenerating
            && !uiInteractionLocked
            && (!showNextSetFallbackButton || IsCanvasInputReady(nextSetFallbackCanvas, nextSetFallbackCanvasGroup))
            && (!showNextCandidateFallbackButton || IsCanvasInputReady(nextCandidateFallbackCanvas, nextCandidateFallbackCanvasGroup));
    }

    private static bool IsCanvasInputReady(Canvas canvas, CanvasGroup group)
    {
        return canvas != null
            && canvas.isActiveAndEnabled
            && canvas.gameObject.activeInHierarchy
            && group != null
            && group.isActiveAndEnabled
            && group.interactable
            && group.blocksRaycasts;
    }

    private void BeginUiInteractionOperation(string operation)
    {
        activeUiOperation = operation;
        activeOperationRemovedCount = 0;
        activeOperationSpawnedCount = 0;
        activeOperationSucceeded = false;
        activeOperationReason = "not-completed";
        isGenerating = true;
        uiInteractionLocked = true;
        try
        {
            SetUiInteractionEnabled(false);
            LogUiInteractionState("candidate-generation-busy", operation);
        }
        catch
        {
            isGenerating = false;
            uiInteractionLocked = false;
            SetUiInteractionEnabled(true);
            throw;
        }
    }

    private void RecordUiInteractionOperationResult(int removed, int spawned, bool success, string reason)
    {
        activeOperationRemovedCount = removed;
        activeOperationSpawnedCount = spawned;
        activeOperationSucceeded = success;
        activeOperationReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
    }

    private void EndUiInteractionOperation(string operation)
    {
        // This is intentionally the single cleanup point for every success, failure,
        // exception, and iterator early-return path.
        isGenerating = false;
        uiInteractionLocked = false;
        SetUiInteractionEnabled(true);
        LogUiInteractionState("generation-complete-immediate", operation);
        Debug.Log(
            $"[AAG UI Execution] operation={operation}; hoverRestored={BoolText(AreAuthoringButtonsHoverReady())}; "
            + $"pinchReadyAfterRelease={BoolText(AreAuthoringButtonsInputReady())}; pinchReleasePending={BoolText(WaitingForPinchRelease)}; "
            + $"removed={activeOperationRemovedCount}; spawned={activeOperationSpawnedCount}; "
            + $"uiRecovered={BoolText(IsUiRecovered())}; success={BoolText(activeOperationSucceeded)}; "
            + $"reason=\"{SanitizeLogReason(activeOperationReason)}\"");
        StartCoroutine(VerifyUiRestoredAfterOperation(
            operation,
            activeOperationRemovedCount,
            activeOperationSpawnedCount,
            activeOperationSucceeded,
            activeOperationReason));
        activeUiOperation = "NONE";
    }

    private IEnumerator VerifyUiRestoredAfterOperation(
        string operation,
        int removed,
        int spawned,
        bool success,
        string reason)
    {
        yield return null;
        var recovered = IsUiRecovered();
        Debug.Log(
            $"[AAG UI AutoCheck] operation={operation}; nextCandidateHover={BoolText(IsButtonHoverReady(nextCandidateFallbackButton, nextCandidateFallbackCanvasGroup))}; "
            + $"nextCandidatePinchAfterRelease={BoolText(IsButtonInputReady(nextCandidateFallbackButton, nextCandidateFallbackCanvasGroup))}; "
            + $"nextSetHover={BoolText(IsButtonHoverReady(nextSetFallbackButton, nextSetFallbackCanvasGroup))}; "
            + $"nextSetPinchAfterRelease={BoolText(IsButtonInputReady(nextSetFallbackButton, nextSetFallbackCanvasGroup))}; "
            + $"statusControlsReady={BoolText(AreSceneStatusControlsReady())}; pinchReleasePending={BoolText(WaitingForPinchRelease)}; "
            + $"uiRecovered={BoolText(recovered)}; removed={removed}; spawned={spawned}; success={BoolText(success)}; "
            + $"reason=\"{SanitizeLogReason(reason)}\"");
        LogUiInteractionState("generation-complete-next-frame", operation);
    }

    private void LogBlockedUiInteraction(string operation, string reason)
    {
        Debug.Log(
            $"[AAG UI Execution] operation={operation}; hoverRestored={BoolText(AreAuthoringButtonsHoverReady())}; "
            + $"pinchReadyAfterRelease={BoolText(AreAuthoringButtonsInputReady())}; pinchReleasePending={BoolText(WaitingForPinchRelease)}; "
            + $"removed=0; spawned=0; uiRecovered={BoolText(IsUiRecovered())}; success=false; "
            + $"reason=\"{SanitizeLogReason(reason)}\"");
        LogUiInteractionState("blocked-operation-complete", operation);
    }

    private void SetUiInteractionEnabled(bool value)
    {
        SetCanvasGroupEnabled(nextSetFallbackCanvasGroup, value);
        SetCanvasGroupEnabled(nextCandidateFallbackCanvasGroup, value);
        if (nextSetFallbackButton != null) nextSetFallbackButton.interactable = value;
        if (nextCandidateFallbackButton != null) nextCandidateFallbackButton.interactable = value;
        if (nextSetFallbackCollider != null) nextSetFallbackCollider.enabled = value;
        if (nextCandidateFallbackCollider != null) nextCandidateFallbackCollider.enabled = value;
    }

    private static void SetCanvasGroupEnabled(CanvasGroup group, bool value)
    {
        if (group == null) return;
        group.interactable = value;
        group.blocksRaycasts = value;
    }

    private bool IsUiRecovered()
    {
        return !isGenerating
            && !uiInteractionLocked
            && AreAuthoringButtonsHoverReady()
            && AreAuthoringButtonsInputReady();
    }

    private bool AreAuthoringButtonsHoverReady()
    {
        return (!showNextSetFallbackButton || IsButtonHoverReady(nextSetFallbackButton, nextSetFallbackCanvasGroup))
            && (!showNextCandidateFallbackButton || IsButtonHoverReady(nextCandidateFallbackButton, nextCandidateFallbackCanvasGroup));
    }

    private bool AreAuthoringButtonsInputReady()
    {
        return (!showNextSetFallbackButton || IsButtonInputReady(nextSetFallbackButton, nextSetFallbackCanvasGroup))
            && (!showNextCandidateFallbackButton || IsButtonInputReady(nextCandidateFallbackButton, nextCandidateFallbackCanvasGroup));
    }

    private static bool IsButtonHoverReady(Button button, CanvasGroup group)
    {
        return button != null
            && button.isActiveAndEnabled
            && button.gameObject.activeInHierarchy
            && group != null
            && group.isActiveAndEnabled
            && group.blocksRaycasts;
    }

    private static bool IsButtonInputReady(Button button, CanvasGroup group)
    {
        return IsButtonHoverReady(button, group) && button.interactable && group.interactable;
    }

    private bool AreSceneStatusControlsReady()
    {
        return FindSceneComponents<Selectable>()
            .Where(selectable => selectable != nextSetFallbackButton && selectable != nextCandidateFallbackButton)
            .All(selectable => !selectable.gameObject.activeInHierarchy || selectable.isActiveAndEnabled);
    }

    private void LogUiInteractionState(string phase, string operation = "NONE")
    {
        var eventSystems = FindSceneComponents<EventSystem>().ToList();
        var modules = FindSceneComponents<BaseInputModule>().ToList();
        var rayInteractors = FindSceneBehaviours(component => component.GetType().Name.IndexOf("RayInteractor", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var interactables = FindSceneBehaviours(component => component.GetType().Name.IndexOf("Interactable", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var leftHit = DescribeFirstPhysicsHit(leftTrackedHand);
        var rightHit = DescribeFirstPhysicsHit(rightTrackedHand);
        var eventIds = eventSystems.Count == 0 ? "NONE" : string.Join(",", eventSystems.Select(item => $"{item.name}#{item.GetInstanceID()}"));

        Debug.Log(
            $"[AAG UI State] phase={phase}; operation={operation}; candidateBusy={BoolText(isGenerating)}; "
            + $"interactionLock={BoolText(uiInteractionLocked)}; activeOperation={activeUiOperation}; "
            + $"candidateDebounceRemaining={Mathf.Max(0f, nextCandidateAllowedTime - Time.unscaledTime):F3}; "
            + $"gripDebounceRemaining={Mathf.Max(0f, nextGripSwitchAllowedTime - Time.unscaledTime):F3}; "
            + $"pinchReleasePending={BoolText(WaitingForPinchRelease)}(L={BoolText(leftPinchReleasePending)},R={BoolText(rightPinchReleasePending)}); "
            + $"pinching=(L={BoolText(IsIndexPinching(leftTrackedHand))},R={BoolText(IsIndexPinching(rightTrackedHand))}); "
            + $"uiRayMask={DescribeLayerMask(AuthoringUiRayMask)}; markerLayer={DescribeLayer(PreviewMarkerLayer)}; "
            + $"uiRoots=(set={DescribeObject(nextSetFallbackRoot)},candidate={DescribeObject(nextCandidateFallbackRoot)}); eventSystems={eventIds}; "
            + $"firstCollider=(L={leftHit},R={rightHit})");

        foreach (var canvas in new[] { nextSetFallbackCanvas, nextCandidateFallbackCanvas }.Where(item => item != null))
        {
            var group = canvas.GetComponent<CanvasGroup>();
            Debug.Log(
                $"[AAG UI Canvas] phase={phase}; name={canvas.name}; instanceId={canvas.GetInstanceID()}; "
                + $"gameObjectActive={BoolText(canvas.gameObject.activeSelf)}; activeInHierarchy={BoolText(canvas.gameObject.activeInHierarchy)}; "
                + $"canvasEnabled={BoolText(canvas.enabled)}; canvasGroupEnabled={BoolText(group != null && group.enabled)}; "
                + $"interactable={BoolText(group != null && group.interactable)}; blocksRaycasts={BoolText(group != null && group.blocksRaycasts)}; layer={DescribeLayer(canvas.gameObject.layer)}");
        }

        foreach (var button in FindSceneComponents<Button>())
        {
            LogComponentState(phase, "Button", button, $"interactable={BoolText(button.interactable)}");
        }
        foreach (var interactable in interactables)
        {
            LogComponentState(phase, "Interactable", interactable, string.Empty);
        }
        foreach (var collider in FindSceneComponents<Collider>())
        {
            LogComponentState(phase, "Collider", collider, $"isTrigger={BoolText(collider.isTrigger)}; layer={DescribeLayer(collider.gameObject.layer)}");
        }
        foreach (var eventSystem in eventSystems)
        {
            LogComponentState(phase, "EventSystem", eventSystem, $"current={BoolText(EventSystem.current == eventSystem)}");
        }
        foreach (var module in modules)
        {
            LogComponentState(phase, "UIInputModule", module, $"eventSystem={DescribeObject(module.GetComponent<EventSystem>()?.gameObject)}");
        }
        foreach (var interactor in rayInteractors)
        {
            LogComponentState(phase, "HandRayInteractor", interactor, $"rayInteractionLayerMask={DescribeLayerMask(AuthoringUiRayMask)}");
        }
    }

    private static void LogComponentState(string phase, string kind, Component component, string extra)
    {
        if (component == null) return;
        var componentEnabled = component is Behaviour behaviour
            ? behaviour.enabled
            : component is Collider collider
                ? collider.enabled
                : true;
        Debug.Log(
            $"[AAG UI Component] phase={phase}; kind={kind}; type={component.GetType().FullName}; name={component.name}; "
            + $"instanceId={component.GetInstanceID()}; gameObjectActive={BoolText(component.gameObject.activeSelf)}; "
            + $"activeInHierarchy={BoolText(component.gameObject.activeInHierarchy)}; componentEnabled={BoolText(componentEnabled)}; {extra}");
    }

    private void LogMarkerUiIdentity(string phase)
    {
        var eventSystem = EventSystem.current;
        Debug.Log(
            $"[AAG Marker/UI Identity] phase={phase}; previewManagedCount={previewObjects.Count}; "
            + $"uiRootSet={DescribeObject(nextSetFallbackRoot)}; uiRootCandidate={DescribeObject(nextCandidateFallbackRoot)}; "
            + $"eventSystem={DescribeObject(eventSystem != null ? eventSystem.gameObject : null)}");
    }

    private bool IsProtectedInteractionObject(GameObject candidate)
    {
        if (candidate == null) return false;
        if (candidate == nextSetFallbackRoot || candidate == nextCandidateFallbackRoot) return true;
        if (candidate.GetComponentInParent<Canvas>(true) != null) return true;
        if (candidate.GetComponentInParent<EventSystem>(true) != null) return true;
        return candidate.GetComponentsInChildren<MonoBehaviour>(true).Any(component =>
            component != null && (component.GetType().Name.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) >= 0
                || component.GetType().Name.IndexOf("EventSystem", StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private void ConfigurePreviewMarkerInteraction(GameObject marker, Collider markerCollider)
    {
        if (marker == null) return;
        SetLayerRecursively(marker, PreviewMarkerLayer);
        if (markerCollider != null)
        {
            markerCollider.enabled = false;
        }
    }

    private void CreateHandRayVisuals()
    {
        if (leftHandRayLine == null)
        {
            CreateHandRayVisual("Left", out leftHandRayLine, out leftHandRayReticle);
        }
        if (rightHandRayLine == null)
        {
            CreateHandRayVisual("Right", out rightHandRayLine, out rightHandRayReticle);
        }
    }

    private void CreateHandRayVisual(string handName, out LineRenderer line, out GameObject reticle)
    {
        var rayObject = new GameObject($"AAG {handName} Hand Ray Interactor Visual");
        line = rayObject.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = 0.004f;
        line.endWidth = 0.002f;
        line.numCapVertices = 4;
        line.startColor = new Color(0.2f, 0.9f, 1f, 0.95f);
        line.endColor = new Color(0.2f, 1f, 0.45f, 0.95f);
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            line.material = new Material(shader) { color = Color.white };
        }
        line.enabled = false;

        reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        reticle.name = $"AAG {handName} Hand Ray Hit Reticle";
        reticle.transform.localScale = Vector3.one * 0.018f;
        SetLayerRecursively(reticle, LayerMask.NameToLayer("Ignore Raycast"));
        var reticleCollider = reticle.GetComponent<Collider>();
        if (reticleCollider != null)
        {
            reticleCollider.enabled = false;
            Destroy(reticleCollider);
        }
        var renderer = reticle.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = new Color(0.2f, 1f, 0.45f, 1f);
        }
        reticle.SetActive(false);
    }

    private void UpdateHandRayVisual(OVRHand hand, LineRenderer line, GameObject reticle)
    {
        if (line == null || reticle == null) return;
        var visible = isReady
            && (nextSetFallbackRoot == null || nextSetFallbackRoot.activeInHierarchy)
            && (nextCandidateFallbackRoot == null || nextCandidateFallbackRoot.activeInHierarchy)
            && hand != null
            && hand.isActiveAndEnabled
            && hand.IsTracked
            && hand.IsPointerPoseValid;
        if (!visible)
        {
            line.enabled = false;
            reticle.SetActive(false);
            return;
        }

        var pose = hand.PointerPose;
        var ray = new Ray(pose.position, pose.forward);
        var hasUiHit = TryGetClosestAuthoringButtonHit(ray, out var hitPoint);
        var endPoint = hasUiHit ? hitPoint : ray.GetPoint(Mathf.Min(AuthoringRayLengthMeters, 1.5f));
        line.SetPosition(0, ray.origin);
        line.SetPosition(1, endPoint);
        line.enabled = true;
        reticle.SetActive(hasUiHit);
        if (hasUiHit)
        {
            reticle.transform.position = hitPoint - ray.direction * 0.003f;
        }
    }

    private bool TryGetClosestAuthoringButtonHit(Ray ray, out Vector3 hitPoint)
    {
        hitPoint = default;
        var found = false;
        var bestDistance = float.PositiveInfinity;
        foreach (var rect in new[] { nextSetFallbackRect, nextCandidateFallbackRect })
        {
            if (rect == null || !rect.gameObject.activeInHierarchy) continue;
            var plane = new Plane(rect.forward, rect.position);
            if (!plane.Raycast(ray, out var distance) || distance < 0f || distance > AuthoringRayLengthMeters) continue;
            var point = ray.GetPoint(distance);
            var local = rect.InverseTransformPoint(point);
            if (!rect.rect.Contains(new Vector2(local.x, local.y)) || distance >= bestDistance) continue;
            bestDistance = distance;
            hitPoint = point;
            found = true;
        }
        return found;
    }

    private string DescribeFirstPhysicsHit(OVRHand hand)
    {
        if (hand == null || !hand.isActiveAndEnabled || !hand.IsTracked || !hand.IsPointerPoseValid)
        {
            return "NO_VALID_HAND_RAY";
        }

        var pose = hand.PointerPose;
        var ray = new Ray(pose.position, pose.forward);
        if (!Physics.Raycast(ray, out var hit, AuthoringRayLengthMeters, ~0, QueryTriggerInteraction.Collide))
        {
            return "NONE";
        }

        var markerIntercept = hit.collider.gameObject.layer == PreviewMarkerLayer;
        if (markerIntercept)
        {
            Debug.LogWarning($"[AAG UI Ray] FIRST HIT IS MARKER name={hit.collider.name}; layer={DescribeLayer(hit.collider.gameObject.layer)}; uiRayMask={DescribeLayerMask(AuthoringUiRayMask)}");
        }
        return $"{hit.collider.name}#{hit.collider.GetInstanceID()}@{DescribeLayer(hit.collider.gameObject.layer)} distance={hit.distance:F3} marker={BoolText(markerIntercept)}";
    }

    private static IEnumerable<T> FindSceneComponents<T>() where T : Component
    {
        return Resources.FindObjectsOfTypeAll<T>().Where(component =>
            component != null && component.gameObject.scene.IsValid());
    }

    private static IEnumerable<MonoBehaviour> FindSceneBehaviours(Func<MonoBehaviour, bool> predicate)
    {
        return Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(component =>
            component != null && component.gameObject.scene.IsValid() && predicate(component));
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        if (root == null || layer < 0) return;
        root.layer = layer;
        foreach (Transform child in root.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }

    private static string DescribeLayer(int layer)
    {
        if (layer < 0) return "INVALID";
        var name = LayerMask.LayerToName(layer);
        return $"{layer}:{(string.IsNullOrEmpty(name) ? "UNNAMED" : name)}";
    }

    private static string DescribeLayerMask(int mask)
    {
        var names = new List<string>();
        for (var layer = 0; layer < 32; layer++)
        {
            if ((mask & (1 << layer)) != 0) names.Add(DescribeLayer(layer));
        }
        return $"0x{mask:X8}[{string.Join(",", names)}]";
    }

    private static string DescribeObject(GameObject value)
    {
        return value == null
            ? "NONE"
            : $"{value.name}#{value.GetInstanceID()} activeSelf={BoolText(value.activeSelf)} activeInHierarchy={BoolText(value.activeInHierarchy)}";
    }
}
