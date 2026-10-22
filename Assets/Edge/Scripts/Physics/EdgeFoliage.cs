using UnityEngine;

namespace Edge
{
    // Marks a trigger collider as soft foliage (tree crowns, tall shrubs): the bat is slowed and
    // jostled while inside instead of being blocked. Added by EDGE > Physics > Add Colliders.
    public class EdgeFoliage : MonoBehaviour
    {
        [Range(0.2f, 1f)] public float speedMultiplier = 0.75f;
        [Range(0f, 1f)] public float instability = 0.35f;
    }
}
