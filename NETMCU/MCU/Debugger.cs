using System.MCU.Compiler.Attributes;

namespace System.MCU
{
    public static class Debugger
    {
        [NativeCall("NETMCU__Debugger__Break")]
        public static extern void Break();
    }
}
