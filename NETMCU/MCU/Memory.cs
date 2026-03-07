using System;
using System.MCU.Compiler.Attributes;

namespace System.MCU
{

    [CompilerType]
    public static class Memory
    {
        [NativeCall("NETMCU__Memory__Write")]
        public static extern void Write(uint addr, uint val);

        [NativeCall("NETMCU__Memory__Read")]
        public static extern uint Read(uint addr);

        [NativeCall("NETMCU__Memory__Alloc")]
        public static extern int Alloc(int size);

        [NativeCall("NETMCU__Memory__Free")]
        public static extern void Free(int ptr);
    }
}
