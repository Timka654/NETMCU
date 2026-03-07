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

        public static bool operator ==(Type left, Type right)
        {
            if (ReferenceEquals(left, right)) return true;
            return false; // Stub
        }

        public static bool operator !=(Type left, Type right) => !(left == right);
    }
}
