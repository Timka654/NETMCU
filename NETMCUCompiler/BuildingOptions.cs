using NETMCUCompiler.CodeBuilder;
using System.Text.Json;

namespace NETMCUCompiler
{
    public sealed class BuildingInputConfigurationModel
    {
        public string Name { get; set; }

        public string Type { get; set; } = "string";

        public string? DefaultValue { get; set; }

        public bool Required { get; set; }

        public string[]? ValidValues { get; set; }

        public Dictionary<string, string>? Messages { get; set; }
    }

    public class BuildingOptions
    {
        public Dictionary<string, string> Configurations { get; set; } = new()
            {
                { "STARTUP_ADDRESS", "0x08008000" },
                { "CORE_PATH", "" }
            };

        public List<string> Include { get; set; } = new();
        public List<string> Libraries { get; set; } = new();
        public List<string> Packages { get; set; } = new();
        public Dictionary<string, string> Defines { get; set; } = new();
        public List<string> Drives { get; set; } = new();

        public List<BuildingInputConfigurationModel> InputConfigurations { get; set; } = new();

    }
}
