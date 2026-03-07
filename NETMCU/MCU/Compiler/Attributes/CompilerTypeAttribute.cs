namespace System.MCU.Compiler.Attributes
{
    /// <summary>
    /// Mark type needs only for compilation (no need interpretation)
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    [CompilerType]
    public class CompilerTypeAttribute : Attribute;
}
