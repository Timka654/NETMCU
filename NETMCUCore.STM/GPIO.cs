using System;
using System.MCU.Compiler.Attributes;

namespace NETMCUCore.STM
{
    public class GPIO
    {
        [NativeCall("HAL_GPIO_WritePin")]
        public static extern void NativeWrite(int port, int pin, int state);

        public void Set()
        {
            NativeWrite(0x40020000, 5, 1);
        }
    }

    public class Dev1
    {
        public void Test()
        {
            GPIO gpio = new GPIO();
            gpio.Set();

            GPIO.NativeWrite(0x40020000, 5, 0);
        }
    }
}
