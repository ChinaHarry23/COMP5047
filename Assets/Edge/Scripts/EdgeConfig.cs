namespace Edge
{
    // Mirrors rev3/firmware/lib/core/edge_config.h. Every number is declared
    // there or in FINAL_PLAN.md; the trailing comment names the source.
    public static class EdgeConfig
    {
        public const int UdpPort = 1234;                    // BUILD_SPEC section 8.1
        public const float FmmPhotLux = 0.2f;               // full-moon illuminance; Gaston 2012 via edge_config.h
        public const float SpMoon = 1.91f;                  // full-moon S/P; Longcore 2018 via edge_config.h
        public const float FmmScotLux = FmmPhotLux * SpMoon; // = 0.382 V'-proxy lux
        public const float SpAmber = 0.23f;                 // lamp endmember S/P, edge_config.h
        public const float TimeCompression = 60f;           // 1 realtime second = 1 night minute
        public const float BMaxFmm = 60f;                   // clear-night budget, edge_config.h (~60 FMM)
        public const float LampPhotLuxAtRef = 4.0f;         // "lamp at max" at the bat, edge_config.h
        public const float LampRefDistanceM = 2f;           // declared reference distance for virtual lamps

        // Disorientation: FINAL_PLAN section 3.2. A stated function of dose,
        // never random(). 0 below onset, linear to full yaw noise at exhaustion.
        public const float DisorientOnsetFraction = 0.6f;
        public const float YawNoiseMaxDegPerSec = 12f;

        public const float CruiseSpeed = 6f;                // m/s, gameplay constant
    }
}
