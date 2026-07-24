using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Creates the fail-closed Room3 AprilTag authoring/alignment setup.</summary>
public static class AagRoom3AprilTagSetup
{
    private const string ScenePath = "Assets/Scenes/MainTest_FP1.unity";
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
    private const float Room3ContentAlongWallBackMeters = 0.5f;
    private const float Room3ContentWallClearanceMeters = 0.4f;

    [MenuItem("AAG/Room3 AprilTag/Setup MainTest FP1 (Preview Only)")]
    public static void SetupMainTestFp1()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
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
        EnsureReferenceCube(referenceObject.transform);

        var experimentMain = Object.FindFirstObjectByType<ExperimentMain>(FindObjectsInactive.Include);
        if (experimentMain == null)
            throw new MissingReferenceException("MainTest_FP1 is missing ExperimentMain.");
        var experiment = experimentMain.gameObject;

        var fixedOffset = experiment.GetComponent<AagFixedSpaceOffset>()
            ?? Undo.AddComponent<AagFixedSpaceOffset>(experiment);
        ConfigureFixedOffset(fixedOffset);

        var aligner = experiment.GetComponent<AagAprilTagTranslationAligner>()
            ?? Undo.AddComponent<AagAprilTagTranslationAligner>(experiment);
        ConfigureAligner(aligner, reference, fixedOffset);

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
        AagFixedSpaceOffset fixedOffset)
    {
        var serialized = new SerializedObject(aligner);
        serialized.FindProperty("room3TagReference").objectReferenceValue = reference;
        serialized.FindProperty("fixedSpaceOffset").objectReferenceValue = fixedOffset;
        serialized.FindProperty("previewOnly").boolValue = true;
        serialized.FindProperty("allowQuestControllerApply").boolValue = false;
        serialized.FindProperty("horizontalOnly").boolValue = false;
        serialized.FindProperty("requireAppliedAlignmentBeforeSession").boolValue = true;
        serialized.FindProperty("contentAlongWallBackMeters").floatValue =
            Room3ContentAlongWallBackMeters;
        serialized.FindProperty("contentWallClearanceMeters").floatValue =
            Room3ContentWallClearanceMeters;
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

    private static void EnsureReferenceCube(Transform referenceTransform)
    {
        var cubeTransform = referenceTransform.Find(ReferenceCubeName);
        GameObject cube;
        if (cubeTransform == null)
        {
            cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = ReferenceCubeName;
            Undo.RegisterCreatedObjectUndo(cube, $"Create {ReferenceCubeName}");
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
            "The initial position is only the supplied example (door open-side +0.70 m, "
            + "floor +1.35 m). Confirm only after checking the real wall installation.",
            MessageType.Warning);
        DrawDefaultInspector();

        if (GUILayout.Button("Capture Floor-Local Pose From Current Transform"))
        {
            Undo.RecordObject(target, "Capture Room3 tag floor-local pose");
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
                ? "Room3 Tag Reference (CONFIRMED)"
                : "Room3 Tag Reference (UNCONFIRMED / PREVIEW ONLY)");
    }
}
