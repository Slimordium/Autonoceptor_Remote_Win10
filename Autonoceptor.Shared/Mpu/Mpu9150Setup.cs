using System;

namespace Autonoceptor.Shared.Mpu
{

    [Flags]
    public enum Mpu9150Setup : byte
    {
        Address = 0x68,
        PowerManagement1 = 0x6B,
        SampleRateDiv = 0x19,
        Config = 0x1A,
        GyroConfig = 0x1B,
        AccelConfig = 0x1C,
        FifoEnable = 0x23,
        InterruptEnable = 0x38,
        InterruptStatus = 0x3A,
        UserCtrl = 0x6A,
        FifoCount = 0x72,
        FifoReadWrite = 0x74
    }
}