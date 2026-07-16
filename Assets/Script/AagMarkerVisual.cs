using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public sealed class AagMarkerVisual : MonoBehaviour
{
    [SerializeField] private Color markerColor = Color.white;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private void OnEnable()
    {
        ApplyColor();
    }

    private void OnValidate()
    {
        ApplyColor();
    }

    private void ApplyColor()
    {
        var propertyBlock = new MaterialPropertyBlock();
        foreach (var targetRenderer in GetComponentsInChildren<Renderer>(true))
        {
            targetRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColorId, markerColor);
            propertyBlock.SetColor(ColorId, markerColor);
            targetRenderer.SetPropertyBlock(propertyBlock);
        }
    }
}
