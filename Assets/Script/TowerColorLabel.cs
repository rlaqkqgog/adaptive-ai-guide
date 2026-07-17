using System;
using TMPro;
using UnityEngine;

/// <summary>World-space label fixed above a tower, for example RED 0/3.</summary>
[DisallowMultipleComponent]
public sealed class TowerColorLabel : MonoBehaviour
{
    private TMP_Text label;
    private string colorName = string.Empty;
    private int deliveredCount;
    private int requiredCount = 3;

    public void Initialize(TMP_Text textComponent, string acceptedColor, int requiredStoneCount)
    {
        label = textComponent;
        colorName = string.IsNullOrWhiteSpace(acceptedColor) ? "unknown" : acceptedColor.Trim().ToLowerInvariant();
        requiredCount = Mathf.Max(1, requiredStoneCount);
        deliveredCount = 0;
        RefreshText();
    }

    public void RecordDelivery()
    {
        deliveredCount = Mathf.Min(requiredCount, deliveredCount + 1);
        RefreshText();
    }

    private void RefreshText()
    {
        if (label == null) return;
        label.text = $"{colorName} {deliveredCount}/{requiredCount}";
        label.color = LabelColor(colorName);
    }

    private static Color LabelColor(string colorName)
    {
        if (string.Equals(colorName, "RED", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.2f, 0.2f);
        if (string.Equals(colorName, "BLUE", StringComparison.OrdinalIgnoreCase)) return new Color(0.25f, 0.55f, 1f);
        if (string.Equals(colorName, "YELLOW", StringComparison.OrdinalIgnoreCase)) return new Color(1f, 0.9f, 0.1f);
        if (string.Equals(colorName, "GREEN", StringComparison.OrdinalIgnoreCase)) return new Color(0.2f, 1f, 0.35f);
        return Color.white;
    }
}
