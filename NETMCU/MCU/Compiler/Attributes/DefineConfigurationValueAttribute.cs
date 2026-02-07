namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class DefineConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Name { get; set; }
        public string? NameArg { get; set; }

        public string? Value { get; set; }
        public string? ValueArg { get; set; }
    }
}
