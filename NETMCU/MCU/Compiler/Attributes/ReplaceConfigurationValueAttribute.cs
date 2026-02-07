using System;
namespace System.MCU.Compiler.Attributes
{
    /// <summary>
    /// Compiler attribute for marking method as one that replaces a configuration value.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class ReplaceConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Name { get; set; }
        public string? NameArg { get; set; }

        public string? Value { get; set; }
        public string? ValueArg { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class InputConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Name { get; set; }
        public string? NameArg { get; set; }

        public bool? Type { get; set; }
        public string? TypeArg { get; set; }

        public string? DefaultValue { get; set; }
        public string? DefaultValueArg { get; set; }

        public bool? Required { get; set; }
        public string? RequiredArg { get; set; }

        
        public string[]? ValidValues { get; set; }
        public string? ValidValuesArg { get; set; }

        public string? ErrorsArg { get; set; }
       
    }
}
