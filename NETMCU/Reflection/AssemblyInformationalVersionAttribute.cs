using System.MCU.Compiler.Attributes;

namespace System.Reflection
{

    [CompilerType]
    public sealed class AssemblyInformationalVersionAttribute(string v) : Attribute { }
}
