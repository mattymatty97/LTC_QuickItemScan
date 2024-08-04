using QuickItemScan.Utils;
using TMPro;
using UnityEngine;

namespace QuickItemScan.Components.ScanElement;

public class ScanElementHolder : MonoBehaviour
{
    public RectTransform RectTransform { get; private set; }
    public Animator Animator { get; private set; }
    public TextMeshProUGUI HeaderText { get; private set; }
    public TextMeshProUGUI SubText { get; private set; }
    
    public NodeIdentifier? AssignedIdentifier { get; internal set; }
    public int? AssignedValue { get; internal set; }
    public int AssignedCount { get; internal set; }
    
    private void Awake()
    {
        RectTransform = GetComponent<RectTransform>();
        Animator = GetComponent<Animator>();
        var texts = GetComponentsInChildren<TextMeshProUGUI>();
        HeaderText = texts[0];
        SubText = texts[1];
        gameObject.SetActive(false);
    }
}