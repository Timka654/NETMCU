using System;
using System.MCU.Compiler.Attributes;

namespace System.Threading
{
    internal class Thread
    {
        [NativeCall("NETMCU__Thread__Sleep")] 
        public static extern void Sleep(int ms);
    }
}
