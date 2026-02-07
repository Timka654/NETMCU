using System;
using System.MCU.Compiler.Attributes;

namespace System.MCU
{
    public static class Memory
    {
        // Компилятор сгенерирует BL Memory_Write
        // Ядро на уровне линковки подставит туда реальный адрес нативного кода
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
