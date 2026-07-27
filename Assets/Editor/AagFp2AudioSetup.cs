using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class AagFp2AudioSetup
{
    private const string ConfigPath = "Assets/Experiment/FP2ExperimentConfig.asset";
    private const string SharedAudioPath = "Assets/Experiment/Audio";
    private const string Fp2AudioPath = SharedAudioPath + "/FP2";

    private sealed class RoomAudioDefinition
    {
        public string roomUuid;
        public string roomId;
        public string aagClipRoomId;
        public string displayName;
        public int tieOrder;
        public string unvisitedCaption;
        public string visitedCaption;
        public string easyCaption;
    }

    [MenuItem("AAG/FP2/Apply Audio Mapping")]
    public static void Apply()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(ConfigPath);
        if (config == null) throw new InvalidOperationException($"Missing FP2 config: {ConfigPath}");

        var definitions = BuildRoomDefinitions();
        var clips = new List<ExperimentAagClip>(32)
        {
            Shared("GF-01", "좀 더 둘러보시겠어요?"),
            Shared("GF-02", "다른 곳도 자유롭게 다녀보세요."),
        };

        foreach (var definition in definitions)
        {
            clips.Add(Fp2("VE-U-", definition.aagClipRoomId, definition.unvisitedCaption));
            clips.Add(Fp2("VE-V-", definition.aagClipRoomId, definition.visitedCaption));
            clips.Add(Fp2("E-", definition.aagClipRoomId, definition.easyCaption));
        }

        clips.Add(Shared("N-01", "아직 찾지 못한 돌이 남아 있어요."));
        clips.Add(Shared("N-02", "남은 돌이 아직 있어요."));
        clips.Add(Shared("H-01", "아직 둘러보지 않은 공간이 남아 있어요."));
        clips.Add(Shared("H-02", "가보지 않은 곳이 아직 있어요."));
        clips.Add(Shared("VH-01", "계속 자유롭게 다녀보세요."));
        clips.Add(Shared("VH-02", "편하신 대로 계속 진행하세요."));

        config.aagClips = clips.ToArray();
        config.rooms = Array.ConvertAll(definitions, definition => new ExperimentRoomMapping
        {
            roomUuid = definition.roomUuid,
            roomId = definition.roomId,
            aagClipRoomId = definition.aagClipRoomId,
            displayName = definition.displayName,
            tieOrder = definition.tieOrder,
        });
        config.RebuildLookups();

        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AagFp2AudioSetup] Bound {config.aagClips.Length} clips and {config.rooms.Length} FP2 rooms.");
    }

    private static RoomAudioDefinition[] BuildRoomDefinitions() => new[]
    {
        Room("0e4e8223-3c13-735b-a552-4acf2ba915a7", "room1", "room414", "414 강의실", 0,
            "아직 안 가보신 414 강의실을 확인해보세요.",
            "414 강의실은 꼼꼼히 살펴보셨나요?",
            "414 강의실 근처도 한번 살펴보세요."),
        Room("d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b", "room2", "room412", "412 강의실", 1,
            "아직 안 가보신 412 강의실을 확인해보세요.",
            "412 강의실은 꼼꼼히 살펴보셨나요?",
            "412 강의실 근처도 한번 살펴보세요."),
        Room("28e81069-81b3-b60a-0166-50599f88ce42", "room3", "slot", "412 강의실 앞쪽", 2,
            "아직 안 가보신 412 강의실 앞쪽을 확인해보세요.",
            "412 강의실 앞쪽은 꼼꼼히 살펴보셨나요?",
            "412 강의실 앞쪽도 한번 살펴보세요."),
        Room("133adc09-ce31-302f-1b53-788b59deeb4f", "room4", "mainhall", "메인 홀", 3,
            "아직 안 가보신 메인 홀을 확인해보세요.",
            "메인 홀은 꼼꼼히 살펴보셨나요?",
            "메인 홀 근처도 한번 살펴보세요."),
        Room("7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b", "room5", "restroom", "화장실 앞쪽", 4,
            "아직 안 가보신 화장실 앞쪽을 확인해보세요.",
            "화장실 앞쪽은 꼼꼼히 살펴보셨나요?",
            "화장실 앞쪽도 한번 살펴보세요."),
        Room("e2e79df2-facb-0150-67b4-a43dfaad9218", "room6", "trash", "쓰레기통쪽 공간", 5,
            "아직 안 가보신 쓰레기통쪽 공간을 확인해보세요.",
            "쓰레기통쪽 공간은 꼼꼼히 살펴보셨나요?",
            "쓰레기통쪽도 한번 살펴보세요."),
        Room("96a223f3-baf3-7044-2958-6f2468b35c72", "room7", "veranda", "베란다 가는 복도", 6,
            "아직 안 가보신 베란다 가는 복도를 확인해보세요.",
            "베란다 가는 복도는 꼼꼼히 살펴보셨나요?",
            "베란다 가는 복도도 한번 살펴보세요."),
        Room("2d4f4c7d-9189-0198-a0ff-ecd07d843c6a", "room8", "room413", "413 강의실", 7,
            "아직 안 가보신 413 강의실을 확인해보세요.",
            "413 강의실은 꼼꼼히 살펴보셨나요?",
            "413 강의실 근처도 한번 살펴보세요."),
    };

    private static RoomAudioDefinition Room(
        string roomUuid,
        string roomId,
        string aagClipRoomId,
        string displayName,
        int tieOrder,
        string unvisitedCaption,
        string visitedCaption,
        string easyCaption) => new RoomAudioDefinition
        {
            roomUuid = roomUuid,
            roomId = roomId,
            aagClipRoomId = aagClipRoomId,
            displayName = displayName,
            tieOrder = tieOrder,
            unvisitedCaption = unvisitedCaption,
            visitedCaption = visitedCaption,
            easyCaption = easyCaption,
        };

    private static ExperimentAagClip Shared(string clipId, string caption) =>
        Clip(clipId, caption, $"{SharedAudioPath}/{clipId}.mp3");

    private static ExperimentAagClip Fp2(string prefix, string roomId, string caption)
    {
        var clipId = prefix + roomId;
        return Clip(clipId, caption, $"{Fp2AudioPath}/{clipId}.mp3");
    }

    private static ExperimentAagClip Clip(string clipId, string caption, string path)
    {
        var audio = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        if (audio == null) throw new InvalidOperationException($"Missing audio for {clipId}: {path}");
        return new ExperimentAagClip { clipId = clipId, captionText = caption, clip = audio };
    }
}
