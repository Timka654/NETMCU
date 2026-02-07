using System.MCU.Compiler.Attributes;

namespace NETMCUCore.STM
{
    public class HAL
    {
        public static void Init() => HAL_API.NativeInit();

        public static void Delay(int milliseconds) => HAL_API.NativeDelay(milliseconds);

    }

    public class HAL_API
    {
        [NativeCall("HAL_Init")]
        public static extern void NativeInit();

        [NativeCall("HAL_Delay")]
        public static extern void NativeDelay(int milliseconds);
    }


    //public class Dev1
    //{
    //    public void Test()
    //    {
    //        GPIO gpio = new GPIO();
    //        gpio.Set();

    //        GPIO.NativeWrite(0x40020000, 5, 0);
    //    }
    //}
}
