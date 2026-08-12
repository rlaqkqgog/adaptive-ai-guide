using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Creates the fail-closed Room3 AprilTag authoring/alignment setup.</summary>
public static class AagRoom3AprilTagSetup
{
    private const string Fp1ScenePath = "Assets/Scenes/MainTest_FP1.unity";
    private const string Fp2ScenePath = "Assets/Scenes/MainTest_FP2.unity";
    private const string SetupRootName = "AAG_Room3_Tag_Alignment";
    private const string AlignmentRootName = "MRUKAlignmentRoot";
    private const string ReferenceName = "Room3_TagReference";
    private const string ReferenceCubeName = "Room3_TagReference_RedCube_10cm";
    private const string ReferenceCubeMaterialPath =
        "Assets/Material/AagRoom3TagReferenceRed.mat";

    // Example from the supplied procedure: 70 cm to the open side of the
    // Room3 door frame and 1.35 m above the exported floor. This remains
    // unconfirmed until the researcher checks the real installation.
    private static readonly Vector3 InitialReferenceWorldPosition =
        new Vector3(-0.89998f, 1.333365f, -2.32729f);
    private const float Room3ContentAlongWallBaselineMeters = -0.65f;
    private const float Room3ContentAlongWallAdjustmentMeters = 0.15f;
    private const float Room3ContentWallClearanceMeters = 0.25f;

    private const string Fp2SetupRootName = "AAG_FP2_Room8_Tag_Alignment";
    private const string Fp2ReferenceName = "FP2_Room8_TagReference";
    private const string Fp2ReferenceCubeName = "FP2_Room8_TagReference_RedCube_10cm";
    private const string Fp2Room8Uuid = "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a";
    private const string Fp2Room8FloorAnchorUuid = "fcd5cb7d-e3ad-844a-9a18-9a6a165124b3";
    private static readonly Vector3 Fp2ExportFloorWorldPosition =
        new Vector3(-0.05573343f, -0.04048982f, 2.3066323f);
    private static readonly Quaternion Fp2ExportFloorWorldRotation =
        new Quaternion(0.64618343f, -0.28713578f, -0.28713575f, -0.6461835f);
    // Installed point selected by the researcher: selected Room8 wall, 0.90 m
    // from its lower plan corner and 1.35 m above the floor.
    private static readonly Vector3 Fp2InitialReferenceWorldPosition =
        new Vector3(-0.02836823f, 1.3095102f, -1.0504057f);
    private static readonly Quaternion Fp2InitialReferenceWorldRotation =
        new Quaternion(0f, 0.4039463f, 0f, 0.9147827f);

    [MenuItem("AAG/Room3 AprilTag/Setup MainTest FP1 (Preview Only)")]
    public static void SetupMainTestFp1()
    {
        var scene = EditorSceneManager.OpenScene(Fp1ScenePath, OpenSceneMode.Single);
        SetupScene(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(
            "[AAG AprilTag Setup] MainTest_FP1 configured. Select Room3_TagReference, "
            + "place its center on the corresponding wall, then confirm the reference in its Inspector.");
    }

    public static void SetupFromCommandLine()
    {
        SetupMainTestFp1();
    }

    [MenuItem("AAG/FP2 AprilTag/Setup MainTest FP2 Room8 (Preview Only)")]
    public static void SetupMainTestFp2()
    {
        var scene = EditorSceneManager.OpenScene(Fp2ScenePath, OpenSceneMode.Single);
        SetupFp2Scene(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log(
            "[AAG AprilTag Setup] MainTest_FP2 configured for tagStandard41h12 ID 1. "
            + "Select FP2_Room8_TagReference, match its center to the installed Room8 tag, "
            + "then confirm the reference in its Inspector.");
    }

    public static void SetupFp2FromCommandLine()
    {
        SetupMainTestFp2();
    }

    [MenuItem("AAG/FP2 AprilTag/Show Room8 Tag Position")]
    public static void ShowFp2Room8TagPosition()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != Fp2ScenePath)
            scene = EditorSceneManager.OpenScene(Fp2ScenePath, OpenSceneMode.Single);

        var setupRoot = GameObject.Find(Fp2SetupRootName);
        var reference = setupRoot == null
            ? null
            : setupRoot.transform.Find($"{AlignmentRootName}/{Fp2ReferenceName}");
        if (reference == null)
        {
            SetupFp2Scene(scene);
            EditorSceneManager.SaveScene(scene);
            setupRoot = GameObject.Find(Fp2SetupRootName);
            reference = setupRoot.transform.Find($"{AlignmentRootName}/{Fp2ReferenceName}");
        }

        Selection.activeTransform = reference;
        EditorGUIUtility.PingObject(reference.gameObject);
        SceneView.lastActiveSceneView?.FrameSelected(false);
        SceneView.RepaintAll();
        Debug.Log(
            "[AAG FP2 AprilTag] Framed the authored Room8 tag center. "
            + "Orange target = tag center; red outline = selected wall. "
            + "Gizmos must be enabled in Scene view.");
    }

    private static void SetupScene(Scene scene)
    {
        var setupRoot = FindOrCreateRoot(scene, SetupRootName);
        setupRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        setupRoot.transform.localScale = Vector3.one;
        if (setupRoot.GetComponent<AagRoom3ReferencePreview>() == null)
            Undo.AddComponent<AagRoom3ReferencePreview>(setupRoot);

        var alignmentRoot = FindOrCreateChild(setupRoot.transform, AlignmentRootName, out _);
        alignmentRoot.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        alignmentRoot.transform.localScale = Vector3.one;

        var referenceObject = FindOrCreateChild(
            alignmentRoot.transform,
            ReferenceName,
            out var referenceCreated);
        if (referenceCreated)
        {
            referenceObject.transform.SetPositionAndRotation(
                InitialReferenceWorldPosition,
                Quaternion.identity);
            referenceObject.transform.localScale = Vector3.one;
        }
        var reference = referenceObject.GetComponent<AagRoom3TagReference>()
            ?? Undo.AddComponent<AagRoom3TagReference>(referenceObject);
        reference.CaptureFloorLocalPoseFromSceneTransform();
        EnsureReferenceCube(referenceObject.transform, ReferenceCubeName);

        var experimentMain = Object.FindFirstObjectByType<ExperimentMain>(FindObjectsInactive.Include);
        if (experimentMain == null)
            throw new MissingReferenceException("MainTest_FP1 is missing ExperimentMain.");
        var experiment = experimentMain.gameObject;

        var fixedOffset = experiment.GetComponent<AagFixedSpaceOffset>()
            ?? Undo.AddComponent<AagFixedSpaceOffset>(experiment);
        ConfigureFixedOffset(fixedOffset);

        var aligner = experiment.GetComponent<AagAprilTagTranslationAligner>()
            ?? Undo.AddComponent<AagAprilTagTranslationAligner>(experiment);
        ConfigureAligner(
            aligner,
            reference,
            fixedOffset,
            0,
            "FP1 ROOM3",
            Room3ContentAlongWallBaselineMeters,
            Room3ContentAlongWallAdjustmentMeters,
            Room3ContentWallClearanceMeters,
            true,
            false,
            false,
            false);

        var experimentSerialized = new SerializedObject(experimentMain);
        experimentSerialized.FindProperty("aprilTagTranslationAligner").objectReferenceValue = aligner;
        experimentSerialized.FindProperty("fixedSpaceOffset").objectReferenceValue = fixedOffset;
        experimentSerialized.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(reference);
        EditorUtility.SetDirty(fixedOffset);
        EditorUtility.SetDirty(aligner);
        EditorUtility.SetDirty(experimentMain);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = referenceObject;
        SceneView.lastActiveSceneView?.FrameSelected();
    }

    private static void SetupFp2Scene(Scene scene)
    {
        var setupRoot = FindOrCreateRoot(scene, Fp2SetupRootName);
        setupRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        setupRoot.transform.localScale = Vector3.one;
        if (setupRoot.GetComponent<AagFp2Room8ReferencePreview>() == null)
            Undo.AddComponent<AagFp2Room8ReferencePreview>(setupRoot);

        var alignmentRoot = FindOrCreateChild(setupRoot.transform, AlignmentRootName, out _);
        alignmentRoot.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        alignmentRoot.transform.localScale = Vector3.one;

        var referenceObject = FindOrCreateChild(
            alignmentRoot.transform,
            Fp2ReferenceName,
            out var referenceCreated);
        if (referenceCreated)
        {
            referenceObject.transform.SetPositionAndRotation(
                Fp2InitialReferenceWorldPosition,
                Fp2InitialReferenceWorldRotation);
            referenceObject.transform.localScale = Vector3.one;
        }
        var reference = referenceObject.GetComponent<AagRoom3TagReference>()
            ?? Undo.AddComponent<AagRoom3TagReference>(referenceObject);
        ConfigureFp2Reference(reference);
        reference.CaptureFloorLocalPoseFromSceneTransform();
        EnsureReferenceCube(referenceObject.transform, Fp2ReferenceCubeName);

        var experimentMain = Object.FindFirstObjectByType<ExperimentMain>(FindObjectsInactive.Include);
        if (experimentMain == null)
            throw new MissingReferenceException("MainTest_FP2 is missing ExperimentMain.");
        var experiment = experimentMain.gameObject;

        var fixedOffset = experiment.GetComponent<AagFixedSpaceOffset>()
            ?? Undo.AddComponent<AagFixedSpaceOffset>(experiment);
        ConfigureFixedOffset(fixedOffset);

        var aligner = experiment.GetComponent<AagAprilTagTranslationAligner>()
            ?? Undo.AddComponent<AagAprilTagTranslationAligner>(experiment);
        ConfigureAligner(
            aligner,
            reference,
            fixedOffset,
            1,
            "FP2 ROOM8",
            0f,
            0f,
            0f,
            false,
            true,
            true,
            true);

        var experimentSerialized = new SerializedObject(experimentMain);
        experimentSerialized.FindProperty("aprilTagTranslationAligner").objectReferenceValue = aligner;
        experimentSerialized.FindProperty("fixedSpaceOffset").objectReferenceValue = fixedOffset;
        experimentSerialized.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(reference);
        EditorUtility.SetDirty(fixedOffset);
        EditorUtility.SetDirty(aligner);
        EditorUtility.SetDirty(experimentMain);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = referenceObject;
        SceneView.lastActiveSceneView?.FrameSelected();
    }

    private static void ConfigureFp2Reference(AagRoom3TagReference reference)
    {
        var serialized = new SerializedObject(reference);
        serialized.FindProperty("referenceLabel").stringValue = "FP2 Room8";
        serialized.FindProperty("referencePlacementConfirmed").boolValue = true;
        serialized.FindProperty("useBakedFloorPose").boolValue = true;
        serialized.FindProperty("roomUuid").stringValue = Fp2Room8Uuid;
        serialized.FindProperty("floorAnchorUuid").stringValue = Fp2Room8FloorAnchorUuid;
        serialized.FindProperty("exportFloorWorldPosition").vector3Value =
            Fp2ExportFloorWorldPosition;
        serialized.FindProperty("exportFloorWorldRotation").quaternionValue =
            Fp2ExportFloorWorldRotation;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ConfigureFixedOffset(AagFixedSpaceOffset fixedOffset)
    {
        var serialized = new SerializedObject(fixedOffset);
        serialized.FindProperty("correctionEnabled").boolValue = false;
        serialized.FindProperty("horizontalOnly").boolValue = false;
        serialized.FindProperty("correctionOffsetMeters").vector3Value = Vector3.zero;
        serialized.FindProperty("applySceneRootsOnStart").boolValue = true;
        var roots = serialized.FindProperty("sceneContentRoots");
        roots.arraySize = 0;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ConfigureAligner(
        AagAprilTagTranslationAligner aligner,
        AagRoom3TagReference reference,
        AagFixedSpaceOffset fixedOffset,
        int expectedTagId,
        string alignmentLabel,
        float alongWallBaselineMeters,
        float alongWallAdjustmentMeters,
        float wallClearanceMeters,
        bool previewOnly,
        bool allowQuestControllerApply,
        bool recordDetectedYaw,
        bool applyDetectedYawRotation)
    {
        var serialized = new SerializedObject(aligner);
        serialized.FindProperty("room3TagReference").objectReferenceValue = reference;
        serialized.FindProperty("fixedSpaceOffset").objectReferenceValue = fixedOffset;
        serialized.FindProperty("expectedTagId").intValue = expectedTagId;
        serialized.FindProperty("tagSizeMeters").floatValue = 0.095f;
        serialized.FindProperty("alignmentLabel").stringValue = alignmentLabel;
        serialized.FindProperty("previewOnly").boolValue = previewOnly;
        serialized.FindProperty("allowQuestControllerApply").boolValue =
            allowQuestControllerApply;
        serialized.FindProperty("horizontalOnly").boolValue = false;
        serialized.FindProperty("requireAppliedAlignmentBeforeSession").boolValue = true;
        serialized.FindProperty("recordDetectedYaw").boolValue = recordDetectedYaw;
        serialized.FindProperty("applyDetectedYawRotation").boolValue =
            applyDetectedYawRotation;
        serialized.FindProperty("maximumStableYawJitterDegrees").floatValue =
            applyDetectedYawRotation ? 3f : 0.75f;
        serialized.FindProperty("contentAlongWallBaselineMeters").floatValue =
            alongWallBaselineMeters;
        serialized.FindProperty("contentAlongWallAdjustmentMeters").floatValue =
            alongWallAdjustmentMeters;
        serialized.FindProperty("contentWallClearanceMeters").floatValue =
            wallClearanceMeters;
        serialized.FindProperty("showRuntimeHud").boolValue = true;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static GameObject FindOrCreateRoot(Scene scene, string name)
    {
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == name) return root;
        var created = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(created, $"Create {name}");
        SceneManager.MoveGameObjectToScene(created, scene);
        return created;
    }

    private static void EnsureReferenceCube(Transform referenceTransform, string cubeName)
    {
        var cubeTransform = referenceTransform.Find(cubeName);
        GameObject cube;
        if (cubeTransform == null)
        {
            cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = cubeName;
            Undo.RegisterCreatedObjectUndo(cube, $"Create {cubeName}");
            cube.transform.SetParent(referenceTransform, false);
        }
        else
        {
            cube = cubeTransform.gameObject;
        }

        cube.transform.SetLocalPositionAndRotation(
            Vector3.forward * 0.08f,
            Quaternion.identity);
        cube.transform.localScale = Vector3.one * 0.1f;

        var collider = cube.GetComponent<Collider>();
        if (collider != null) Undo.DestroyObjectImmediate(collider);

        var renderer = cube.GetComponent<MeshRenderer>();
        if (renderer != null) renderer.sharedMaterial = GetOrCreateReferenceCubeMaterial();
        EditorUtility.SetDirty(cube);
    }

    private static Material GetOrCreateReferenceCubeMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(ReferenceCubeMaterialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Standard");
            if (shader == null)
                throw new MissingReferenceException("No supported shader was found for the red reference cube.");

            material = new Material(shader)
            {
                name = "AagRoom3TagReferenceRed"
            };
            AssetDatabase.CreateAsset(material, ReferenceCubeMaterialPath);
        }

        material.color = Color.red;
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.red);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static GameObject FindOrCreateChild(Transform parent, string name, out bool createdNew)
    {
        var existing = parent.Find(name);
        if (existing != null)
        {
            createdNew = false;
            return existing.gameObject;
        }
        var created = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(created, $"Create {name}");
        created.transform.SetParent(parent, false);
        createdNew = true;
        return created;
    }
}

public static class AagFp2Room8ReferencePreviewGizmo
{
    [DrawGizmo(GizmoType.Active | GizmoType.Selected | GizmoType.NonSelected)]
    private static void DrawTagPosition(
        AagFp2Room8ReferencePreview preview,
        GizmoType gizmoType)
    {
        var reference = preview.transform.Find(
            "MRUKAlignmentRoot/FP2_Room8_TagReference");
        if (reference == null) return;

        var position = reference.position;
        var right = reference.right * 0.0855f;
        var up = reference.up * 0.0855f;
        var normal = reference.forward;

        Handles.color = new Color(1f, 0.55f, 0.05f, 1f);
        Handles.DrawAAPolyLine(
            6f,
            position - right - up,
            position + right - up,
            position + right + up,
            position - right + up,
            position - right - up);
        Handles.DrawWireDisc(position, normal, 0.14f, 4f);
        Handles.DrawLine(position, position + normal * 0.45f, 5f);
        Handles.SphereHandleCap(
            0,
            position,
            Quaternion.identity,
            0.055f,
            EventType.Repaint);

        var style = new GUIStyle(EditorStyles.boldLabel)
        {
            normal = { textColor = new Color(1f, 0.55f, 0.05f, 1f) },
            fontSize = 14
        };
        Handles.Label(
            position + Vector3.up * 0.24f,
            "FP2 ROOM8 · APRILTAG ID 1\nPRINT CENTER · H=1.35m",
            style);
    }
}

[CustomEditor(typeof(AagRoom3TagReference))]
public sealed class AagRoom3TagReferenceEditor : Editor
{
    public override void OnInspectorGUI()
    {
        EditorGUILayout.HelpBox(
            "Move this transform so its origin is the AprilTag detection-square center, "
            + "not the printed board center. White square = 95 mm detection border; "
            + "orange/green square = 171 mm printed board.",
            MessageType.Info);
        EditorGUILayout.HelpBox(
            "The initial position is only an authored estimate. Confirm only after checking "
            + "the real wall installation for "
            + ((AagRoom3TagReference)target).ReferenceLabel + ".",
            MessageType.Warning);
        DrawDefaultInspector();

        if (GUILayout.Button("Capture Floor-Local Pose From Current Transform"))
        {
            Undo.RecordObject(target, "Capture tag floor-local pose");
            ((AagRoom3TagReference)target).CaptureFloorLocalPoseFromSceneTransform();
            EditorUtility.SetDirty(target);
        }
    }

    private void OnSceneGUI()
    {
        var reference = (AagRoom3TagReference)target;
        Handles.color = reference.ReferencePlacementConfirmed
            ? new Color(0.15f, 1f, 0.35f)
            : new Color(1f, 0.65f, 0.1f);
        Handles.Label(
            reference.transform.position + Vector3.up * 0.16f,
            reference.ReferencePlacementConfirmed
                ? $"{reference.ReferenceLabel} Tag Reference (CONFIRMED)"
                : $"{reference.ReferenceLabel} Tag Reference (UNCONFIRMED / PREVIEW ONLY)");
    }
}
