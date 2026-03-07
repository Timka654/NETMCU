
using System.MCU.Compiler.Attributes;

namespace System.Runtime.Versioning
{
    [AttributeUsage(AttributeTargets.Assembly, Inherited = false)]

    [CompilerType]
    public sealed class TargetFrameworkAttribute : Attribute
    {
        public TargetFrameworkAttribute(string frameworkName)
        {
            FrameworkName = frameworkName;
        }
        public string FrameworkName { get; }
        public string FrameworkDisplayName { get; set; }
    }
}