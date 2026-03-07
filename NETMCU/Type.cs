using System.MCU.Compiler.Attributes;

namespace System
{
    [CompilerType]
    public abstract class Type 
    { 
        public static Type GetTypeFromHandle(RuntimeTypeHandle handle)
        {
            return null; // Stub
        }
    }
}
