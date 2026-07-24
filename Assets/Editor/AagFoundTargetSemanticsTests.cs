using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class AagFoundTargetSemanticsTests
{
    [Test]
    public void BuildAagTargets_PocketedTarget_IsProjectedAsFound()
    {
        var mainObject = new GameObject("experiment main");
        try
        {
            var main = mainObject.AddComponent<ExperimentMain>();
            var targetStateType = typeof(ExperimentMain).GetNestedType(
                "TargetState",
                BindingFlags.NonPublic);
            Assert.That(targetStateType, Is.Not.Null);

            var state = Activator.CreateInstance(targetStateType);
            SetField(state, "objectId", "red_1");
            SetField(state, "roomUuid", "room-uuid");
            SetField(state, "roomId", "room1");
            SetField(state, "pocketed", true);

            var targetsField = typeof(ExperimentMain).GetField(
                "targets",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(targetsField, Is.Not.Null);
            var targetStates = targetsField.GetValue(main) as IDictionary;
            Assert.That(targetStates, Is.Not.Null);
            targetStates.Add("red_1", state);

            var method = typeof(ExperimentMain).GetMethod(
                "BuildAagTargets",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var projection = ((IEnumerable)method.Invoke(main, null))
                .Cast<AagGuideTarget>()
                .Single();

            Assert.That(projection.delivered, Is.True);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(mainObject);
        }
    }

    [Test]
    public void NotifyTargetFound_AddsAesEvidenceAndResetsLostness()
    {
        var metricsObject = new GameObject("behavior metrics");
        var loggerObject = new GameObject("logging manager");
        var headObject = new GameObject("head");
        var config = ScriptableObject.CreateInstance<Fp1ExperimentConfig>();
        try
        {
            config.aesWindowSeconds = 24f;
            config.aesMinimumDistanceMeters = 12f;
            config.aesMinimumUniqueRooms = 2;
            config.aesMinimumHeadRotationDegrees = 500f;
            config.proxyWindowSeconds = 48f;
            config.proxyColdStartRatio = 0.5f;

            var metrics = metricsObject.AddComponent<BehaviorMetrics>();
            var logger = loggerObject.AddComponent<LoggingManager>();
            metrics.BeginSession(config, headObject.transform, logger);
            SetField(metrics, "currentLostness", 0.8f);

            Assert.That(metrics.NotifyTargetFound("red_1", "inventory"), Is.True);
            var snapshot = metrics.Evaluate(ExperimentGuideMode.AAG);

            Assert.That(snapshot.aesRecentFoundTargets, Is.EqualTo(1));
            Assert.That(snapshot.aesClearlyPassive, Is.False);
            Assert.That(GetField<float>(metrics, "currentLostness"), Is.Zero);
            Assert.That(metrics.NotifyTargetFound("red_1", "delivery"), Is.False,
                "The later delivery must not count the same found target twice.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(config);
            UnityEngine.Object.DestroyImmediate(headObject);
            UnityEngine.Object.DestroyImmediate(loggerObject);
            UnityEngine.Object.DestroyImmediate(metricsObject);
        }
    }

    [Test]
    public void HasRemainingTargets_AllPocketedProjection_ReturnsFalse()
    {
        var targets = new List<AagGuideTarget>
        {
            new AagGuideTarget { objectId = "red_1", delivered = true },
            new AagGuideTarget { objectId = "blue_1", delivered = true },
        };

        Assert.That(InvokeHasRemainingTargets(targets), Is.False);
    }

    [Test]
    public void HasRemainingTargets_OneUnfoundTarget_ReturnsTrue()
    {
        var targets = new List<AagGuideTarget>
        {
            new AagGuideTarget { objectId = "red_1", delivered = true },
            new AagGuideTarget { objectId = "blue_1", delivered = false },
        };

        Assert.That(InvokeHasRemainingTargets(targets), Is.True);
    }

    private static bool InvokeHasRemainingTargets(IReadOnlyCollection<AagGuideTarget> targets)
    {
        var method = typeof(AAGGuide).GetMethod(
            "HasRemainingTargets",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        return (bool)method.Invoke(null, new object[] { targets });
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        field.SetValue(instance, value);
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        return (T)field.GetValue(instance);
    }
}
