using System;

namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    [CompilerType]
    public class NativeCallAttribute(string name) : Attribute
    {
    }
}
