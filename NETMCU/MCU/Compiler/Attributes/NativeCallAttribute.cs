using System;

namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public class NativeCallAttribute(string name) : Attribute
    {
    }
}
