using UnityEngine;

namespace Edge.Input
{
    // Casiez et al. 2012 One Euro filter: smooths jitter at rest, near-zero lag when moving.
    public class OneEuroFilter
    {
        public float minCutoff, beta, dCutoff;
        float _x, _dx;
        bool _init;

        public OneEuroFilter(float minCutoff = 1.5f, float beta = 0.05f, float dCutoff = 1f)
        { this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff; }

        static float Alpha(float cutoff, float dt) { float tau = 1f / (2f * Mathf.PI * cutoff); return 1f / (1f + tau / dt); }

        public void Reset(float x) { _x = x; _dx = 0f; _init = true; }

        public float Filter(float x, float dt)
        {
            if (!_init || dt <= 0f) { Reset(x); return x; }
            float dx = (x - _x) / dt;
            _dx = Mathf.Lerp(_dx, dx, Alpha(dCutoff, dt));
            float cutoff = minCutoff + beta * Mathf.Abs(_dx);
            _x = Mathf.Lerp(_x, x, Alpha(cutoff, dt));
            return _x;
        }
    }
}
