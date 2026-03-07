namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    [CompilerType]
    public class IncludeConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Include { get; set; }
        public string? IncludeArg { get; set; }
    }
}
