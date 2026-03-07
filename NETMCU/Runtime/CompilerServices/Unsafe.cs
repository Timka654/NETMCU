using System.MCU.Compiler.Attributes;

namespace System.Runtime.CompilerServices
{
    [CompilerType]
    public static class Unsafe
    {
        [NativeCall("NETMCU__Unsafe__AsPointer")]
        public static unsafe extern void* AsPointer(object value);

        [NativeCall("NETMCU__Unsafe__As")]
        public static unsafe extern T As<T>(void* value);

        [NativeCall("NETMCU__Unsafe__As")]
        public static unsafe extern T As<T>(object value);
    }
}