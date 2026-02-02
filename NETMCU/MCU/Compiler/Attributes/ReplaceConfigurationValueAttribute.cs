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
    public class IncludeConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Include { get; set; }
        public string? IncludeArg { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class DriveConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Path { get; set; }
        public string? PathArg { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class PackageConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Name { get; set; }
        public string? NameArg { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class LibraryConfigurationValueAttribute: MCUConfigurationValueAttribute
    {
        public string? Path { get; set; }
        public string? PathArg { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class DefineConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Name { get; set; }
        public string? NameArg { get; set; }

        public string? Value { get; set; }
        public string? ValueArg { get; set; }
    }
}
