using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

public sealed class AagFp2AudioConfigTests
{
    private const string ConfigPath = "Assets/Experiment/FP2ExperimentConfig.asset";

    [Test]
    public void Fp2Config_BindsEightSharedAndTwentyFourRoomClips()
    {
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(ConfigPath);

        Assert.That(config, Is.Not.Null);
        Assert.That(config.aagClips, Has.Length.EqualTo(32));
        Assert.That(config.aagClips.Select(binding => binding.clipId).Distinct().Count(), Is.EqualTo(32));
        foreach (var binding in config.aagClips)
        {
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding.clip, Is.Not.Null, $"Missing AudioClip: {binding.clipId}");
            Assert.That(binding.captionText, Is.Not.Empty, $"Missing caption: {binding.clipId}");
        }

        var sharedIds = new[] { "GF-01", "GF-02", "N-01", "N-02", "H-01", "H-02", "VH-01", "VH-02" };
        foreach (var clipId in sharedIds)
        {
            var binding = config.FindAagClip(clipId);
            Assert.That(binding, Is.Not.Null, $"Missing shared clip binding: {clipId}");
            Assert.That(AssetDatabase.GetAssetPath(binding.clip), Is.EqualTo($"Assets/Experiment/Audio/{clipId}.mp3"));
        }
    }

    [Test]
    public void Fp2Config_MapsEveryRoomToItsNamedAudioSet()
    {
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(ConfigPath);
        Assert.That(config, Is.Not.Null);

        var expected = new[]
        {
            (uuid: "0e4e8223-3c13-735b-a552-4acf2ba915a7", roomId: "room1", audioId: "room414", name: "414 강의실"),
            (uuid: "d45cc90a-b2c1-b189-efe9-d58eb2f4cf7b", roomId: "room2", audioId: "room412", name: "412 강의실"),
            (uuid: "28e81069-81b3-b60a-0166-50599f88ce42", roomId: "room3", audioId: "slot", name: "412 강의실 앞쪽"),
            (uuid: "133adc09-ce31-302f-1b53-788b59deeb4f", roomId: "room4", audioId: "mainhall", name: "메인 홀"),
            (uuid: "7d466842-a3fd-ca0c-bcc1-595d9ddfcf0b", roomId: "room5", audioId: "restroom", name: "화장실 앞쪽"),
            (uuid: "e2e79df2-facb-0150-67b4-a43dfaad9218", roomId: "room6", audioId: "trash", name: "쓰레기통쪽 공간"),
            (uuid: "96a223f3-baf3-7044-2958-6f2468b35c72", roomId: "room7", audioId: "veranda", name: "베란다 가는 복도"),
            (uuid: "2d4f4c7d-9189-0198-a0ff-ecd07d843c6a", roomId: "room8", audioId: "room413", name: "413 강의실"),
        };

        Assert.That(config.rooms, Has.Length.EqualTo(expected.Length));
        foreach (var value in expected)
        {
            var room = config.FindRoom(value.uuid);
            Assert.That(room, Is.Not.Null, $"Missing FP2 room mapping: {value.uuid}");
            Assert.That(room.roomId, Is.EqualTo(value.roomId));
            Assert.That(room.ResolveAagClipRoomId(), Is.EqualTo(value.audioId));
            Assert.That(room.displayName, Is.EqualTo(value.name));

            foreach (var prefix in new[] { "VE-U-", "VE-V-", "E-" })
            {
                var clipId = prefix + value.audioId;
                var binding = config.FindAagClip(clipId);
                Assert.That(binding, Is.Not.Null, $"Missing FP2 clip binding: {clipId}");
                Assert.That(
                    AssetDatabase.GetAssetPath(binding.clip),
                    Is.EqualTo($"Assets/Experiment/Audio/FP2/{clipId}.mp3"));
            }
        }
    }

    [Test]
    public void EmptyAudioRoomAlias_FallsBackToStableRoomId()
    {
        var room = new ExperimentRoomMapping { roomId = "room1" };
        Assert.That(room.ResolveAagClipRoomId(), Is.EqualTo("room1"));
    }

    [Test]
    public void Fp2Config_UsesSmallSpaceDirectGuidanceCalibration()
    {
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(ConfigPath);

        Assert.That(config, Is.Not.Null);
        Assert.That(config.initialSupportLevel, Is.EqualTo(AagSupportLevel.VeryEasy));
        Assert.That(config.aesWindowSeconds, Is.EqualTo(16f));
        Assert.That(config.aesMinimumDistanceMeters, Is.EqualTo(6f));
        Assert.That(config.aesMinimumHeadRotationDegrees, Is.EqualTo(360f));
        Assert.That(config.proxyWindowSeconds, Is.EqualTo(24f));
        Assert.That(config.proxyLowBoundary, Is.EqualTo(0.2f));
        Assert.That(config.proxyHighBoundary, Is.EqualTo(0.55f));
        Assert.That(config.minimumUtteranceGapSeconds, Is.EqualTo(12f));
        Assert.That(config.stalestRecencyFloorSeconds, Is.EqualTo(30f));
        Assert.That(config.stalestMeaningfulDwellSeconds, Is.EqualTo(3f));
    }
}
