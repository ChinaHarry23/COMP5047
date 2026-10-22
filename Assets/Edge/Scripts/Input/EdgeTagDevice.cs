using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Edge.Input
{
    // Memory layout of one EdgeTag input-state event. steer/climb are already
    // calibrated (-1..1) by EdgeTagInput; roll/pitch/yaw/gyro are the raw tag values.
    [StructLayout(LayoutKind.Explicit, Size = 36)]
    public struct EdgeTagState : IInputStateTypeInfo
    {
        public static FourCC Format => new FourCC('E', 'D', 'G', 'T');
        public FourCC format => Format;

        [InputControl(name = "steer", layout = "Axis")]                 [FieldOffset(0)]  public float steer;
        [InputControl(name = "climb", layout = "Axis")]                 [FieldOffset(4)]  public float climb;
        [InputControl(name = "roll", layout = "Axis", noisy = true)]    [FieldOffset(8)]  public float roll;   // deg
        [InputControl(name = "pitch", layout = "Axis", noisy = true)]   [FieldOffset(12)] public float pitch;  // deg
        [InputControl(name = "yaw", layout = "Axis", noisy = true)]     [FieldOffset(16)] public float yaw;    // deg
        [InputControl(name = "gyro", layout = "Vector3", noisy = true)] [FieldOffset(20)] public Vector3 gyro; // deg/s
        [InputControl(name = "zero", layout = "Button", bit = 0, displayName = "Zero (tag button)")]
        [FieldOffset(32)] public uint buttons;
    }

    // The bat tag as a first-class Input System device ("<EdgeTag>/steer" etc.).
    [InputControlLayout(stateType = typeof(EdgeTagState), displayName = "EDGE Tag (ESP32 + MPU-6050)")]
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    public class EdgeTag : InputDevice
    {
        public const string Product = "EDGE Tag";
        public AxisControl steer { get; private set; }
        public AxisControl climb { get; private set; }
        public AxisControl roll { get; private set; }
        public AxisControl pitch { get; private set; }
        public AxisControl yaw { get; private set; }
        public Vector3Control gyro { get; private set; }
        public ButtonControl zero { get; private set; }

        static EdgeTag() { Register(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            InputSystem.RegisterLayout<EdgeTag>(matches: new InputDeviceMatcher().WithProduct(Product));
        }

        protected override void FinishSetup()
        {
            base.FinishSetup();
            steer = GetChildControl<AxisControl>("steer");
            climb = GetChildControl<AxisControl>("climb");
            roll = GetChildControl<AxisControl>("roll");
            pitch = GetChildControl<AxisControl>("pitch");
            yaw = GetChildControl<AxisControl>("yaw");
            gyro = GetChildControl<Vector3Control>("gyro");
            zero = GetChildControl<ButtonControl>("zero");
        }

        public static EdgeTag AddOrGet()
        {
            foreach (var d in InputSystem.devices)
                if (d is EdgeTag t) return t;
            return (EdgeTag)InputSystem.AddDevice(new InputDeviceDescription { interfaceName = "EDGE-UDP", product = Product });
        }
    }
}
