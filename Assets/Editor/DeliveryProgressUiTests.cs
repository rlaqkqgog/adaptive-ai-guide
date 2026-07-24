using System;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class DeliveryProgressUiTests
{
    [Test]
    public void ShowingDeliveryText_HidesLobbyAndDisablesUnexpectedBackground()
    {
        var mainObject = new GameObject("experiment main");
        var lobby = new GameObject("lobby");
        var textObject = new GameObject(
            "delivery progress", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        try
        {
            var main = mainObject.AddComponent<ExperimentMain>();
            var text = textObject.GetComponent<TextMeshProUGUI>();
            var unexpectedMask = textObject.AddComponent<Mask>();
            lobby.SetActive(true);

            SetField(main, "lobbyPanel", lobby);
            SetField(main, "deliveryProgressText", text);
            var stateField = typeof(ExperimentMain).GetField(
                "state", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(stateField, Is.Not.Null);
            stateField.SetValue(main, Enum.Parse(stateField.FieldType, "Running"));

            Invoke(main, "SetDeliveryProgressText", "DELIVERY PROCESSING...\n3", true);

            Assert.That(lobby.activeSelf, Is.False);
            Assert.That(textObject.activeSelf, Is.True);
            Assert.That(unexpectedMask.enabled, Is.False);
            Assert.That(text.text, Is.EqualTo("DELIVERY PROCESSING...\n3"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(textObject);
            UnityEngine.Object.DestroyImmediate(lobby);
            UnityEngine.Object.DestroyImmediate(mainObject);
        }
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {name}");
        field.SetValue(instance, value);
    }

    private static void Invoke(object instance, string name, params object[] arguments)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method {name}");
        method.Invoke(instance, arguments);
    }
}
