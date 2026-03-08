using System;
using System.Collections.Generic;
using System.Text;

namespace NETMCUCompiler.Shared.Attributes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
    public class MCUCompilationBackendAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }
}
