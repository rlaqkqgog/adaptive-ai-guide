using NUnit.Framework;
using UnityEditor;

public sealed class AagTimingConfigTests
{
    [Test]
    public void ProductionConfig_ScalesAagTimingToFortyPercentOfOriginalOnly()
    {
        var config = AssetDatabase.LoadAssetAtPath<Fp1ExperimentConfig>(
            "Assets/Experiment/FP1ExperimentConfig.asset");

        Assert.That(config, Is.Not.Null);
        Assert.That(config.aesWindowSeconds, Is.EqualTo(24f));
        Assert.That(config.decisionIntervalSeconds, Is.EqualTo(2f));
        Assert.That(config.proxyWindowSeconds, Is.EqualTo(12f));
        Assert.That(config.proxyColdStartRatio, Is.EqualTo(0.5f));
        Assert.That(config.proxyWindowSeconds * config.proxyColdStartRatio, Is.EqualTo(6f));
        Assert.That(config.minimumUtteranceGapSeconds, Is.EqualTo(8f));

        Assert.That(config.aesMinimumDistanceMeters, Is.EqualTo(12f));
        Assert.That(config.aesMinimumUniqueRooms, Is.EqualTo(2));
        Assert.That(config.aesMinimumHeadRotationDegrees, Is.EqualTo(2800f));
        Assert.That(config.sessionTimeLimitSeconds, Is.EqualTo(1200f));
        Assert.That(config.trackIntervalSeconds, Is.EqualTo(0.1f));
        Assert.That(config.stalestRecencyFloorSeconds, Is.EqualTo(60f));
        Assert.That(config.stalestMeaningfulDwellSeconds, Is.EqualTo(5f));
    }
}
