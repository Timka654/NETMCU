using System.MCU.Compiler.Attributes;

namespace System.Reflection
{

    [CompilerType]
    public sealed class AssemblyFileVersionAttribute(string v) : Attribute { }
}
