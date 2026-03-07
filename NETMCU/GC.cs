using System.MCU.Compiler.Attributes;

namespace System
{
    [CompilerType]
    public static class GC
    {
        public static void Collect()
        {
            // Note: C implementation for real GC isn't there yet,
            // we will bind this to a native function eventually, e.g. NETMCU__Memory__Collect
            CollectNative();
        }

        [NativeCall("NETMCU__Memory__Collect")]
        private static extern void CollectNative();

        public static long GetTotalMemory(bool forceFullCollection)
        {
            if (forceFullCollection)
            {
                Collect();
            }
            return 0; // Stub
        }

        public static void SuppressFinalize(object obj)
        {
            // Nothing yet
        }
        
        public static void ReRegisterForFinalize(object obj)
        {
            // Nothing yet
        }
    }
}