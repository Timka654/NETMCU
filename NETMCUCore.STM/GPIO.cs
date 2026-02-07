using System;
using System.MCU.Compiler.Attributes;

namespace NETMCUCore.STM
{
    internal class GPIO
    {
        [NativeCall("HAL_GPIO_WritePin")]
        private static extern void NativeWrite(int port, int pin, int state);

        public void Set()
        {
            NativeWrite(0x40020000, 5, 1);
        }
    }
}
