namespace System.MCU.Compiler.Attributes
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class DriveConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Path { get; set; }
        public string? PathArg { get; set; }
        public string? ContainerPath { get; set; }
        public string? ContainerPathArg { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class RepositoryConfigurationValueAttribute : MCUConfigurationValueAttribute
    {
        public string? Path { get; set; }
        public string? PathArg { get; set; }
        public string? Url { get; set; }
        public string? UrlArg { get; set; }
        public string? Branch { get; set; }
        public string? BranchArg { get; set; }
        public int? Depth { get; set; }
        public string? DepthArg { get; set; }
    }
}
