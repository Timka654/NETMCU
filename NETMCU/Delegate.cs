
using System.MCU.Compiler.Attributes;

namespace System
{
    [CompilerType]
    public class Delegate 
    { 
        public object Target;
        public IntPtr MethodPtr;
    }
}
