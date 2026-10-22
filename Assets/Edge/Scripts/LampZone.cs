using UnityEngine;

namespace Edge
{
    // A virtual path lamp. Inverse-square falloff from the declared reference:
    // at EdgeConfig.LampRefDistanceM it delivers EDGE_LAMP_PHOT_LUX_AT_MAX to
    // the bat — the level the firmware FSM calls "lamp at max" (edge_config.h).
    // Proximity events go to the tag so the handset buzzes near virtual lamps.
    public class LampZone : MonoBehaviour
    {
        public int id;
        public Transform bat;
        public Transform measureFrom;
        public DoseModel dose;
        public UdpLink link;
        public float radius = 12f;

        bool _inside;
        float _sendTimer;

        void Update()
        {
            if (!bat || !dose) return;
            float dt = Time.deltaTime;
            Vector3 origin = measureFrom ? measureFrom.position : transform.position;
            float dist = Vector3.Distance(bat.position, origin);
            bool inside = dist < radius;

            if (inside)
            {
                float dd = Mathf.Max(dist, 0.5f);
                float lux = EdgeConfig.LampPhotLuxAtRef *
                            (EdgeConfig.LampRefDistanceM * EdgeConfig.LampRefDistanceM) / (dd * dd);
                dose.AddVirtualExposure(lux, dt);

                _sendTimer += dt;
                if (link && (!_inside || _sendTimer >= 0.1f))
                {
                    _sendTimer = 0f;
                    float prox = 1f - Mathf.Clamp01(dist / radius);
                    link.Send($"LAMP {id} {prox:F2}");
                }
            }
            else if (_inside && link)
            {
                link.Send($"LAMP {id} 0");
            }
            _inside = inside;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.5f);
            Vector3 origin = measureFrom ? measureFrom.position : transform.position;
            Gizmos.DrawWireSphere(origin, radius);
        }
    }
}
