using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class AagTimingConfigTests
{
    [Test]
    public void ProductionConfig_UsesCalibratedAagThresholds()
    {
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(
            "Assets/Experiment/FP1ExperimentConfig.asset");

        Assert.That(config, Is.Not.Null);
        Assert.That(config.aesWindowSeconds, Is.EqualTo(24f));
        Assert.That(config.decisionIntervalSeconds, Is.EqualTo(2f));
        Assert.That(config.proxyWindowSeconds, Is.EqualTo(48f));
        Assert.That(config.proxyColdStartRatio, Is.EqualTo(0.5f));
        Assert.That(config.proxyWindowSeconds * config.proxyColdStartRatio, Is.EqualTo(24f));
        Assert.That(config.proxyLowBoundary, Is.EqualTo(0.25f));
        Assert.That(config.proxyHighBoundary, Is.EqualTo(0.71f));
        Assert.That(config.minimumUtteranceGapSeconds, Is.EqualTo(15f));

        Assert.That(config.aesMinimumDistanceMeters, Is.EqualTo(12f));
        Assert.That(config.aesMinimumUniqueRooms, Is.EqualTo(2));
        Assert.That(config.aesMinimumHeadRotationDegrees, Is.EqualTo(500f));
        Assert.That(config.sessionTimeLimitSeconds, Is.EqualTo(1200f));
        Assert.That(config.trackIntervalSeconds, Is.EqualTo(0.1f));
        Assert.That(config.stalestRecencyFloorSeconds, Is.EqualTo(60f));
        Assert.That(config.stalestMeaningfulDwellSeconds, Is.EqualTo(5f));
    }

    [Test]
    public void ProductionConfig_BindsEveryRoomSpecificAagClip()
    {
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(
            "Assets/Experiment/FP1ExperimentConfig.asset");

        Assert.That(config, Is.Not.Null);
        var roomIds = new[]
        {
            "room1", "room2-1", "room2-2", "room3",
            "hall1-1", "hall1-2", "hall1-3", "hall2-1", "hall2-2",
        };

        foreach (var roomId in roomIds)
        {
            foreach (var prefix in new[] { "VE-U-", "VE-V-", "E-" })
            {
                var clipId = prefix + roomId;
                var binding = config.FindAagClip(clipId);
                Assert.That(binding, Is.Not.Null, $"Missing AAG clip binding: {clipId}");
                Assert.That(binding.clip, Is.Not.Null, $"Missing AudioClip asset: {clipId}");
                Assert.That(binding.captionText, Is.Not.Empty, $"Missing caption: {clipId}");
            }
        }
    }

    [Test]
    public void TargetFound_PassesAesAndStartsFreshLostnessEpisode()
    {
        var root = new GameObject("AAG target-found metrics test");
        var config = ScriptableObject.CreateInstance<Fp1ExperimentConfig>();
        try
        {
            config.aesWindowSeconds = 24f;
            config.proxyWindowSeconds = 48f;
            config.proxyColdStartRatio = 0.5f;
            var logger = root.AddComponent<LoggingManager>();
            var metrics = root.AddComponent<BehaviorMetrics>();
            metrics.BeginSession(config, root.transform, logger);

            for (var index = 0; index < 240; index++)
                metrics.Sample(0.1f, ExperimentGuideMode.AAG);
            Assert.That(metrics.Evaluate(ExperimentGuideMode.AAG).proxyHasValue, Is.True);

            Assert.That(metrics.NotifyTargetFound("red_1", "inventory"), Is.True);
            var afterFind = metrics.Evaluate(ExperimentGuideMode.AAG);
            Assert.That(afterFind.aesRecentFoundTargets, Is.EqualTo(1));
            Assert.That(afterFind.aesGatePassed, Is.True);
            Assert.That(afterFind.proxyHasValue, Is.False);
            Assert.That(afterFind.proxyColdStart, Is.True);
            Assert.That(afterFind.proxyRatio, Is.Zero);
            Assert.That(metrics.NotifyTargetFound("red_1", "delivery"), Is.False);
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(config);
        }
    }
}
