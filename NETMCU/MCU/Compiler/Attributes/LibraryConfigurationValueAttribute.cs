namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class LibraryConfigurationValueAttribute: MCUConfigurationValueAttribute
    {
        public string? Path { get; set; }
        public string? PathArg { get; set; }
    }
}
