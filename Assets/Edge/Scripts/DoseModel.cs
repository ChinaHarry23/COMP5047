using System.Globalization;
using UnityEngine;

namespace Edge
{
    // The one budget (FINAL_PLAN section 3.1). Virtual lamps accumulate here;
    // real light arrives from the tag over UDP. Same units, same account.
    public class DoseModel : MonoBehaviour
    {
        public UdpLink link;

        public float RealFmm { get; private set; }
        public float VirtualFmm { get; private set; }
        public float BudgetTonight { get; private set; } = EdgeConfig.BMaxFmm;
        public float CloudC { get; private set; }

        public float TotalFmm => RealFmm + VirtualFmm;
        public float Fraction => BudgetTonight > 0.001f ? TotalFmm / BudgetTonight : 0f;
        public bool Exhausted => Fraction >= 1f;

        // FINAL_PLAN section 3.2: the stated function. Zero below the onset
        // fraction, linear to full instability at exhaustion. Never random().
        public float Instability01
        {
            get
            {
                float f = Fraction;
                if (f <= EdgeConfig.DisorientOnsetFraction) return 0f;
                return Mathf.Clamp01((f - EdgeConfig.DisorientOnsetFraction) /
                                     (1f - EdgeConfig.DisorientOnsetFraction));
            }
        }

        void Start()
        {
            if (link) link.OnMessage += Handle;
        }

        void OnDestroy()
        {
            if (link) link.OnMessage -= Handle;
        }

        void Handle(string msg)
        {
            // DOSE <fmm> <budgetRemaining> <cTonight> from the tag, ~10 Hz
            var p = msg.Split(' ');
            if (p.Length >= 2 && p[0] == "DOSE" &&
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fmm))
            {
                RealFmm = fmm;
                if (p.Length >= 3 &&
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float rem))
                    BudgetTonight = Mathf.Max(0.001f, RealFmm + rem);
                if (p.Length >= 4 &&
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float c))
                    CloudC = c;
            }
        }

        /// Forget the virtual-lamp dose (the feeding game starts each round from a clean night).
        public void ResetVirtual() { VirtualFmm = 0f; }

        // photLux: photopic lux the virtual lamp delivers at the bat right now.
        // Converted with the lamp endmember S/P, exactly as dose.c does.
        public void AddVirtualExposure(float photLux, float dt)
        {
            VirtualFmm += photLux * EdgeConfig.SpAmber / EdgeConfig.FmmScotLux
                          * (EdgeConfig.TimeCompression / 60f) * dt;
        }
    }
}
