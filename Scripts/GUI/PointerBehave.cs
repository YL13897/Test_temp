using UnityEngine;
using UnityEngine.UI;

public class PointerBehave : MonoBehaviour
{
    [SerializeField] Transform pointerTransform;
    [SerializeField] Transform leaderTransform;
    [SerializeField] SpriteRenderer fillSpriteRenderer;
    [SerializeField] float alignedHalfWidth = 1f;
    [SerializeField] Color alignedColor = Color.green;
    [SerializeField] Color misalignedColor = Color.red;

    void Awake()
    {
        pointerTransform ??= transform;
        fillSpriteRenderer ??= GetComponent<SpriteRenderer>();
    }

    void Update()
    {
        PointerBehaveUpdate();
    }

    void PointerBehaveUpdate()
    {
        // if (pointerTransform == null || leaderTransform == null) return;
        float pointerX = pointerTransform.position.x;
        float leaderX = leaderTransform.position.x;
        
        // --- Previous Logic ---
        // ------------------------------------------------------------------
        // bool isAligned = Mathf.Abs(pointerX - leaderX) <= alignedHalfWidth;
        // Color targetColor = isAligned ? alignedColor : misalignedColor;
        // ------------------------------------------------------------------


        // --- New Logic: Fade to lighter color (white) as it deviates ---
        // ------------------------------------------------------------------
        float deviation = Mathf.Abs(pointerX - leaderX);
        float deviationRatio = Mathf.Clamp01(deviation / alignedHalfWidth);
        
        // Lerp from the aligned color towards white to make it lighter
        Color targetColor = Color.Lerp(alignedColor, Color.white, deviationRatio);
        
        // Optionally snap to misaligned color if fully outside the band
        if (deviation > alignedHalfWidth)
        {
            targetColor = misalignedColor;
        }
        // ------------------------------------------------------------------

        fillSpriteRenderer.color = targetColor;
    }
}
