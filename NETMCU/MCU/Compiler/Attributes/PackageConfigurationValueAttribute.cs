namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    [CompilerType]
    public class PackageConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Name { get; set; }
        public string? NameArg { get; set; }
    }
}
