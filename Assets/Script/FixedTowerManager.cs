using System;
using System.Collections.Generic;
using Oculus.Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds the four destination pagodas on spatial anchors shared by every
/// experiment set. FixedTowerAnchorLoader exclusively owns the anchor shells;
/// this class only adds the pagoda, label, and delivery zone below them.
/// </summary>
[DisallowMultipleComponent]
public sealed class FixedTowerManager : MonoBehaviour
{
    public const int RequiredTowerCount = 4;

    private Fp1ExperimentConfig config;
    private ExperimentMain experimentMain;
    private readonly List<GameObject> spawnedTowerContents = new List<GameObject>();

    public bool IsConfigured { get; private set; }

    public void Initialize(
        Fp1ExperimentConfig experimentConfig,
        ExperimentMain owner)
    {
        config = experimentConfig;
        experimentMain = owner;
    }

    public bool TryBuildAnchorMap(
        out Dictionary<Guid, ExperimentTowerAnchor> towersByUuid,
        out string failure)
    {
        towersByUuid = new Dictionary<Guid, ExperimentTowerAnchor>();
        failure = string.Empty;
        IsConfigured = false;

        if (config != null && !config.fixedTowersEnabled) return true;

        var storedManifest = AagFixedTowerAnchorStore.LoadOrCreate();
        var storedTowers = storedManifest.towers ?? new List<AagFixedTowerAnchorEntry>();
        if (storedTowers.Count > 0)
        {
            if (storedTowers.Count != RequiredTowerCount)
            {
                failure = $"fixed_tower_authoring_incomplete_{storedTowers.Count}_{RequiredTowerCount}";
                return false;
            }

            foreach (var towerId in AagFixedTowerAnchorStore.TowerIds)
            {
                var stored = storedTowers.Find(value => string.Equals(value.tower_id, towerId, StringComparison.Ordinal));
                if (stored == null || !Guid.TryParse(stored.anchor_uuid, out var uuid) || uuid == Guid.Empty)
                {
                    failure = $"fixed_tower_store_invalid_{towerId}";
                    return false;
                }
                if (towersByUuid.ContainsKey(uuid))
                {
                    failure = $"fixed_tower_store_duplicate_uuid_{towerId}";
                    return false;
                }

                towersByUuid.Add(uuid, BuildRuntimeDefinition(towerId, stored));
            }

            IsConfigured = true;
            Debug.Log(
                $"[FixedTower] configurationSource=PERSISTENT_STORE path={AagFixedTowerAnchorStore.ManifestPath} " +
                $"towers={towersByUuid.Count}", this);
            return true;
        }

        var definitions = config != null
            ? config.fixedTowers ?? Array.Empty<ExperimentTowerAnchor>()
            : Array.Empty<ExperimentTowerAnchor>();
        var populatedCount = 0;
        foreach (var definition in definitions)
        {
            if (definition != null && !string.IsNullOrWhiteSpace(definition.anchorUuid))
                populatedCount++;
        }

        // Empty UUIDs are an intentional authoring state. Existing experiments can
        // still run while the four physical tower anchors are being captured.
        if (populatedCount == 0)
        {
            Debug.LogWarning(
                $"[FixedTower] No saved tower UUIDs. Use TOWER MODE to save Tower-1..4. " +
                $"store={AagFixedTowerAnchorStore.ManifestPath}", this);
            return true;
        }

        if (definitions.Length != RequiredTowerCount || populatedCount != RequiredTowerCount)
        {
            failure = $"fixed_towers_require_4_uuids_actual_{populatedCount}";
            return false;
        }

        var towerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var towerId = definition.towerId?.Trim();
            if (string.IsNullOrEmpty(towerId) || !towerIds.Add(towerId))
            {
                failure = "fixed_tower_id_missing_or_duplicate";
                return false;
            }
            if (!Guid.TryParse(definition.anchorUuid, out var uuid) || uuid == Guid.Empty)
            {
                failure = $"fixed_tower_invalid_uuid_{towerId}";
                return false;
            }
            if (towersByUuid.ContainsKey(uuid))
            {
                failure = $"fixed_tower_duplicate_uuid_{towerId}";
                return false;
            }
            towersByUuid.Add(uuid, definition);
        }

        IsConfigured = true;
        return true;
    }

    public bool TrySpawnLocalizedTowers(
        FixedTowerAnchorLoader towerAnchorLoader,
        IReadOnlyDictionary<Guid, ExperimentTowerAnchor> towersByUuid,
        out string failure)
    {
        failure = string.Empty;
        ClearRuntimeContents();
        if (towersByUuid == null || towersByUuid.Count == 0) return true;

        var resourcePath = config == null || string.IsNullOrWhiteSpace(config.fixedTowerResourcesPath)
            ? "Prefabs/StonepagodaTower"
            : config.fixedTowerResourcesPath.Trim();
        var pagodaPrefab = Resources.Load<GameObject>(resourcePath);
        if (pagodaPrefab == null)
        {
            failure = $"fixed_tower_prefab_missing_Resources/{resourcePath}";
            return false;
        }

        foreach (var pair in towersByUuid)
        {
            if (towerAnchorLoader == null
                || !towerAnchorLoader.TryGetTransform(pair.Key, out var anchorTransform)
                || towerAnchorLoader.Failures.ContainsKey(pair.Key))
            {
                failure = $"fixed_tower_anchor_missing_{pair.Value.towerId}";
                ClearRuntimeContents();
                return false;
            }

            SpawnTowerContents(anchorTransform, pair.Value, pagodaPrefab);
        }

        Debug.Log($"[FixedTower] Spawn complete towers={spawnedTowerContents.Count}/{RequiredTowerCount}", this);
        return true;
    }

    public void ClearRuntimeContents()
    {
        foreach (var instance in spawnedTowerContents)
            if (instance != null) Destroy(instance);
        spawnedTowerContents.Clear();
    }

    private void SpawnTowerContents(
        Transform anchorTransform,
        ExperimentTowerAnchor definition,
        GameObject pagodaPrefab)
    {
        PrepareInvisibleAnchorShell(anchorTransform.gameObject);
        anchorTransform.gameObject.name = $"FixedTowerAnchor {definition.towerId}";

        var container = new GameObject($"{definition.towerId} Runtime");
        container.transform.SetParent(anchorTransform, false);
        spawnedTowerContents.Add(container);

        var pagoda = Instantiate(pagodaPrefab, container.transform, false);
        pagoda.name = $"{definition.towerId} Stonepagoda";
        pagoda.transform.localPosition = definition.visualLocalPosition;
        pagoda.transform.localRotation = Quaternion.Euler(definition.visualLocalEulerAngles);
        pagoda.transform.localScale = definition.visualLocalScale;
        EnvironmentDepthOcclusion.ApplyToRenderers(pagoda.transform);

        var colorLabel = CreateColorLabel(container.transform, definition);

        var deliveryObject = new GameObject($"{definition.towerId} DeliveryZone");
        deliveryObject.transform.SetParent(container.transform, false);
        deliveryObject.transform.localPosition = definition.deliveryZoneCenter;
        var trigger = deliveryObject.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = PositiveSize(definition.deliveryZoneSize);
        var rigidbody = deliveryObject.AddComponent<Rigidbody>();
        rigidbody.isKinematic = true;
        rigidbody.useGravity = false;
        var zone = deliveryObject.AddComponent<TowerDeliveryZone>();
        zone.Initialize(
            experimentMain,
            definition.towerId,
            definition.acceptedColor,
            definition.requireGrabBeforeDelivery,
            colorLabel);
    }

    private static TowerColorLabel CreateColorLabel(Transform parent, ExperimentTowerAnchor definition)
    {
        var labelObject = new GameObject(
            $"{definition.towerId} ColorLabel",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(TowerColorLabel));
        var rect = labelObject.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        // Keep the label outside the pagoda hierarchy so its world size does
        // not inherit the pagoda model scale. It remains fixed in tower space.
        rect.localPosition = definition.visualLocalPosition + definition.labelLocalPosition;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one * 0.01f;
        rect.sizeDelta = new Vector2(
            Mathf.Max(10f, definition.labelWidthMeters * 100f),
            Mathf.Max(10f, definition.labelHeightMeters * 100f));

        var canvas = labelObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 50;
        labelObject.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 100f;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        var textRect = textObject.GetComponent<RectTransform>();
        textRect.SetParent(rect, false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var text = textObject.GetComponent<TextMeshProUGUI>();
        text.alignment = TextAlignmentOptions.Center;
        text.fontStyle = FontStyles.Bold;
        text.enableAutoSizing = false;
        text.fontSize = 15f; // 70% smaller than the previous 0.5 m world-space label.
        text.outlineColor = Color.black;
        text.outlineWidth = 0.25f;
        EnvironmentDepthOcclusion.ApplyToText(text);

        var label = labelObject.GetComponent<TowerColorLabel>();
        label.Initialize(text, definition.acceptedColor, definition.requiredStoneCount);
        return label;
    }

    private static void PrepareInvisibleAnchorShell(GameObject anchorObject)
    {
        foreach (var renderer in anchorObject.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        foreach (var canvas in anchorObject.GetComponentsInChildren<Canvas>(true))
            canvas.enabled = false;
        foreach (var collider in anchorObject.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        foreach (var grabbable in anchorObject.GetComponentsInChildren<Grabbable>(true))
            grabbable.enabled = false;
        foreach (var rigidbody in anchorObject.GetComponentsInChildren<Rigidbody>(true))
        {
            rigidbody.useGravity = false;
            rigidbody.isKinematic = true;
        }
    }

    private static Vector3 PositiveSize(Vector3 size)
    {
        return new Vector3(
            Mathf.Max(0.01f, Mathf.Abs(size.x)),
            Mathf.Max(0.01f, Mathf.Abs(size.y)),
            Mathf.Max(0.01f, Mathf.Abs(size.z)));
    }

    private ExperimentTowerAnchor BuildRuntimeDefinition(string towerId, AagFixedTowerAnchorEntry stored)
    {
        ExperimentTowerAnchor configured = null;
        if (config?.fixedTowers != null)
        {
            configured = Array.Find(config.fixedTowers,
                value => value != null && string.Equals(value.towerId, towerId, StringComparison.Ordinal));
        }

        return new ExperimentTowerAnchor
        {
            towerId = towerId,
            acceptedColor = !string.IsNullOrWhiteSpace(configured?.acceptedColor)
                ? configured.acceptedColor
                : AagFixedTowerAnchorStore.ColorForTowerId(towerId),
            anchorUuid = stored.anchor_uuid,
            hasFallbackPose = stored.has_fallback_pose,
            fallbackWorldPosition = new Vector3(stored.fallback_x, stored.fallback_y, stored.fallback_z),
            fallbackWorldRotation = new Quaternion(
                stored.fallback_rotation_x,
                stored.fallback_rotation_y,
                stored.fallback_rotation_z,
                stored.fallback_rotation_w),
            visualLocalPosition = configured?.visualLocalPosition ?? Vector3.zero,
            visualLocalEulerAngles = configured?.visualLocalEulerAngles ?? Vector3.zero,
            visualLocalScale = configured?.visualLocalScale ?? Vector3.one * 0.1f,
            labelLocalPosition = configured?.labelLocalPosition ?? new Vector3(0f, 2f, 0f),
            labelWidthMeters = configured?.labelWidthMeters ?? 1.8f,
            labelHeightMeters = configured?.labelHeightMeters ?? 0.5f,
            requiredStoneCount = configured?.requiredStoneCount ?? 3,
            deliveryZoneCenter = configured?.deliveryZoneCenter ?? new Vector3(0f, 0.75f, 0f),
            deliveryZoneSize = configured?.deliveryZoneSize ?? new Vector3(1.5f, 1.5f, 1.5f),
            requireGrabBeforeDelivery = configured?.requireGrabBeforeDelivery ?? true,
        };
    }
}
