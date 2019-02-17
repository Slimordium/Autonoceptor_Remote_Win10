namespace Autonoceptor.Host.Hardware
{
    public class AutonoDataFrame
    {
        public double Yaw { get; set; }
        public double Pitch { get; set; }
        public double Roll { get; set; }
        public double CmToTarget { get; set; }
        public double LidarSignalStrength { get; set; }
        public double RpmTarget { get; set; }
        public double RpmActual { get; set; }
        public bool ImuCalibrated { get; set; }
        public bool PixySig1 { get; set; }
        public float PixySig1X { get; set; }
        public float PixySig1Y { get; set; }
    }

    public class AutonoCommand
    {
        public ushort Command { get; set; }

        public ushort CommandValue { get; set; }

        public bool CommandBoolValue { get; set; }

    }

    public enum Command : byte
    {
        SetSteeringPwm = 0,
        SetThrottlePwm,
        SetRpmTarget,
        PixySteeringEnabled,
        GetCurrentTelemetryFrame,
        TelemetryStreamEnabled,
        GetImuCalibrationStatus,
        SetYawOffset,
        SetSteeringOffset,
        SetThrottleAutomatic,
        LidarEnabled,
        FollowMeEnabled
    }
}