using System.Linq;
using NUnit.Framework;
using UnityEngine;

public sealed class IncidentalPrefabVisibilityTests
{
    [TestCase("IncidentalObjects/FP1-S2/05_sewingMachin")]
    [TestCase("IncidentalObjects/FP1-S3/03_heamer")]
    public void Prefab_HasAnActiveVisibleRenderer(string resourcePath)
    {
        var prefab = Resources.Load<GameObject>(resourcePath);
        Assert.That(prefab, Is.Not.Null, resourcePath);
        var instance = Object.Instantiate(prefab);
        try
        {
            instance.SetActive(true);
            var visible = instance.GetComponentsInChildren<Renderer>(true)
                .Where(value => value != null && value.enabled && value.gameObject.activeInHierarchy)
                .ToArray();
            Assert.That(visible, Is.Not.Empty, $"No active renderer in {resourcePath}");
            Assert.That(visible.Max(value => value.bounds.size.magnitude), Is.GreaterThan(0.05f));
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }
}
