using UnityEngine;

public class RowDisplayTemplate : MonoBehaviour
{
    [SerializeField]
    private CardDisplayTemplate imageDisplayTemplate;

    [SerializeField]
    private CardDisplayTemplate textDisplayTemplate;

    public XrProbe.Result source { get; set; } 

    public CardDisplayTemplate imageCard { get  { return imageDisplayTemplate; } }
}
