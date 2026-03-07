using System.MCU.Compiler.Attributes;

namespace System.Reflection
{

    [CompilerType]
    public sealed class AssemblyVersionAttribute(string v) : Attribute { }
}
